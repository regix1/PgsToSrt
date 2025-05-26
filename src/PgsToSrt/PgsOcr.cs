using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Nikse.SubtitleEdit.Core.Common;
using Nikse.SubtitleEdit.Core.SubtitleFormats;
using PgsToSrt.BluRaySup;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using TesseractOCR;
using TesseractOCR.Enums;

namespace PgsToSrt;

public class PgsOcr
{
    private readonly Microsoft.Extensions.Logging.ILogger _logger;
    private readonly Subtitle _subtitle = new ();
    private readonly string _tesseractVersion;
    private readonly string _libLeptName;
    private readonly string _libLeptVersion;
    private List<BluRaySupParserImageSharp.PcsData> _bluraySubtitles;

    public string TesseractDataPath { get; set; }
    public string TesseractLanguage { get; set; } = "eng";
    public string CharacterBlacklist { get; set; }

    public PgsOcr(Microsoft.Extensions.Logging.ILogger logger, string tesseractVersion, string libLeptName, string libLeptVersion)
    {
        _logger = logger;
        _tesseractVersion = tesseractVersion;
        _libLeptName = libLeptName;
        _libLeptVersion = libLeptVersion;
    }

    public bool ToSrt(List<BluRaySupParserImageSharp.PcsData> subtitles, string outputFileName)
    {
        _bluraySubtitles = subtitles;

        if (!DoOcrParallel())
            return false;

        try
        {
            Save(outputFileName);
            _logger.LogInformation($"Saved '{outputFileName}' with {_subtitle.Paragraphs.Count} items.");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Saving '{outputFileName}' failed:");
            return false;
        }
    }

    private void Save(string outputFileName)
    {
        using var file = new StreamWriter(outputFileName, false, new UTF8Encoding(false));
        file.Write(_subtitle.ToText(new SubRip()));
    }

    private bool DoOcrParallel()
    {
        _logger.LogInformation($"Starting OCR for {_bluraySubtitles.Count} items...");
        _logger.LogInformation($"Tesseract version {_tesseractVersion}");

        if (!string.IsNullOrEmpty(CharacterBlacklist))
        {
            _logger.LogInformation($"Character blacklist: {CharacterBlacklist}");
        }

        var exception = TesseractApi.Initialize(_tesseractVersion, _libLeptName, _libLeptVersion);
        if (exception != null)
        {
            _logger.LogError(exception, $"Failed: {exception.Message}");
            return false;
        }

        var ocrResults = new ConcurrentBag<Paragraph>();

        // Process subtitles in parallel
        Parallel.ForEach(Enumerable.Range(0, _bluraySubtitles.Count), i =>
        {
            try
            {
                var item = _bluraySubtitles[i];

                var text = GetText(i);
                
                // Only add non-empty results
                if (!string.IsNullOrWhiteSpace(text))
                {
                    var paragraph = new Paragraph
                    {
                        Number = i + 1,
                        StartTime = new TimeCode(item.StartTime / 90.0),
                        EndTime = new TimeCode(item.EndTime / 90.0),
                        Text = text
                    };

                    ocrResults.Add(paragraph);
                }

                if (i % 50 == 0)
                {
                    _logger.LogInformation($"Processed item {i + 1}.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error processing item {i}: {ex.Message}");
            }
        });

        // Sort the results by number and add them to the subtitle
        var sortedResults = ocrResults.OrderBy(p => p.Number).ToList();
        
        // Merge subtitles that are too close together or overlapping
        var mergedResults = MergeCloseSubtitles(sortedResults);
        
        _subtitle.Paragraphs.AddRange(mergedResults);

        _logger.LogInformation($"Finished OCR. Found {mergedResults.Count} valid subtitles out of {_bluraySubtitles.Count} processed (merged from {sortedResults.Count}).");
        return true;
    }

    private string GetText(int index)
    {
        try
        {
            using var bitmap = GetSubtitleBitmap(index);
            if (bitmap == null || bitmap.Width == 0 || bitmap.Height == 0)
            {
                return string.Empty;
            }

            // Try two different approaches and pick the better result
            var result1 = TryOcrMethod1(bitmap);
            var result2 = TryOcrMethod2(bitmap);

            return ChooseBestResult(result1, result2);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error in GetText for index {index}: {ex.Message}");
            return string.Empty;
        }
    }

    private string TryOcrMethod1(Image<Rgba32> bitmap)
    {
        try
        {
            // Method 1: Basic approach with Auto page segmentation
            using var engine = new Engine(TesseractDataPath, TesseractLanguage);
            
            if (!string.IsNullOrEmpty(CharacterBlacklist))
                engine.SetVariable("tessedit_char_blacklist", CharacterBlacklist);
            
            using var image = GetPix(bitmap);
            using var page = engine.Process(image, PageSegMode.Auto);
            
            return page.Text?.Trim() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private string TryOcrMethod2(Image<Rgba32> bitmap)
    {
        try
        {
            // Method 2: Light preprocessing + SingleBlock segmentation
            using var processedBitmap = PreprocessImageForOcr(bitmap);
            using var engine = new Engine(TesseractDataPath, TesseractLanguage);
            
            if (!string.IsNullOrEmpty(CharacterBlacklist))
                engine.SetVariable("tessedit_char_blacklist", CharacterBlacklist);
            
            // Use SingleBlock for cleaner text extraction
            engine.SetVariable("tessedit_pageseg_mode", "6");
            
            using var image = GetPix(processedBitmap);
            using var page = engine.Process(image, PageSegMode.SingleBlock);
            
            return page.Text?.Trim() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private string ChooseBestResult(string result1, string result2)
    {
        // If one is empty, return the other
        if (string.IsNullOrWhiteSpace(result1)) return result2 ?? string.Empty;
        if (string.IsNullOrWhiteSpace(result2)) return result1;

        // Prefer results that seem more like real text
        var score1 = ScoreTextQuality(result1);
        var score2 = ScoreTextQuality(result2);

        return score2 > score1 ? result2 : result1;
    }

    private static int ScoreTextQuality(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;

        int score = 0;
        
        // Basic length bonus (longer is often better for subtitles)
        score += Math.Min(text.Length, 100); // Cap at 100 to avoid just preferring very long garbage
        
        // Letter ratio bonus (more letters = better)
        int letters = text.Count(char.IsLetter);
        int total = text.Length;
        if (total > 0)
        {
            double letterRatio = (double)letters / total;
            score += (int)(letterRatio * 50); // Up to 50 bonus points
        }
        
        // Word count bonus (real text has reasonable word breaks)
        var wordCount = text.Split(new[] { ' ', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries).Length;
        score += Math.Min(wordCount * 5, 25); // Up to 25 bonus points
        
        // Penalty for excessive special characters (not in blacklist, so shouldn't be there)
        int specialChars = text.Count(c => !char.IsLetterOrDigit(c) && !char.IsWhiteSpace(c) && 
                                          !".,!?'-:;()[]{}\"".Contains(c));
        score -= specialChars * 3; // 3 point penalty per bad special char
        
        return Math.Max(0, score);
    }

    private List<Paragraph> MergeCloseSubtitles(List<Paragraph> paragraphs)
    {
        if (paragraphs.Count <= 1) return paragraphs;

        var merged = new List<Paragraph>();
        var current = paragraphs[0];

        for (int i = 1; i < paragraphs.Count; i++)
        {
            var next = paragraphs[i];
            
            // Check if subtitles should be merged
            if (ShouldMerge(current, next))
            {
                // Merge the current and next subtitle
                current = MergeSubtitles(current, next);
            }
            else
            {
                // Add current to results and move to next
                merged.Add(current);
                current = next;
            }
        }
        
        // Add the last subtitle
        merged.Add(current);

        // Renumber the merged subtitles
        for (int i = 0; i < merged.Count; i++)
        {
            merged[i].Number = i + 1;
        }

        return merged;
    }

    private static bool ShouldMerge(Paragraph current, Paragraph next)
    {
        // Calculate time gap between subtitles
        var gap = next.StartTime.TotalMilliseconds - current.EndTime.TotalMilliseconds;
        
        // Merge if overlapping or very close (within 500ms)
        if (gap <= 500)
        {
            return true;
        }
        
        // Check for text similarity that suggests they're the same subtitle split
        if (gap <= 2000) // Within 2 seconds
        {
            var currentText = current.Text.ToLower().Trim();
            var nextText = next.Text.ToLower().Trim();
            
            // If one text contains the other, or they start similarly, merge them
            if (currentText.Contains(nextText) || nextText.Contains(currentText) ||
                (currentText.Length > 10 && nextText.Length > 10 && 
                 currentText.Substring(0, Math.Min(10, currentText.Length)) == 
                 nextText.Substring(0, Math.Min(10, nextText.Length))))
            {
                return true;
            }
        }
        
        return false;
    }

    private static Paragraph MergeSubtitles(Paragraph first, Paragraph second)
    {
        // Choose the better text (longer and higher quality)
        var firstScore = ScoreTextQuality(first.Text);
        var secondScore = ScoreTextQuality(second.Text);
        
        string mergedText;
        if (firstScore > secondScore * 1.2) // First is significantly better
        {
            mergedText = first.Text;
        }
        else if (secondScore > firstScore * 1.2) // Second is significantly better
        {
            mergedText = second.Text;
        }
        else
        {
            // Similar quality, combine them intelligently
            var firstText = first.Text.Trim();
            var secondText = second.Text.Trim();
            
            // If one contains the other, use the longer one
            if (firstText.Contains(secondText))
            {
                mergedText = firstText;
            }
            else if (secondText.Contains(firstText))
            {
                mergedText = secondText;
            }
            else
            {
                // Combine them with a line break
                mergedText = firstText + "\n" + secondText;
            }
        }

        return new Paragraph
        {
            Number = first.Number,
            StartTime = new TimeCode(Math.Min(first.StartTime.TotalMilliseconds, second.StartTime.TotalMilliseconds)),
            EndTime = new TimeCode(Math.Max(first.EndTime.TotalMilliseconds, second.EndTime.TotalMilliseconds)),
            Text = mergedText
        };
    }

    private static Image<Rgba32> PreprocessImageForOcr(Image<Rgba32> original)
    {
        var processed = original.Clone();
        
        try
        {
            // Light preprocessing - just grayscale and modest scaling
            processed.Mutate(x => x
                .Grayscale()              // Convert to grayscale
                .Resize(original.Width * 2, original.Height * 2) // Scale up 2x for better OCR
            );
        }
        catch
        {
            // If preprocessing fails, return original
            processed.Dispose();
            return original.Clone();
        }
        
        return processed;
    }

    private static TesseractOCR.Pix.Image GetPix(Image<Rgba32> bitmap)
    {
        byte[] bytes;
        using (var stream = new MemoryStream())
        {
            bitmap.SaveAsBmp(stream);
            bytes = stream.ToArray();
        }
        return TesseractOCR.Pix.Image.LoadFromMemory(bytes);
    }

    private Image<Rgba32> GetSubtitleBitmap(int index)
    {
        try
        {
            if (index < 0 || index >= _bluraySubtitles.Count)
                return null;

            return _bluraySubtitles[index].GetRgba32();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error getting subtitle bitmap for index {index}: {ex.Message}");
            return null;
        }
    }
}