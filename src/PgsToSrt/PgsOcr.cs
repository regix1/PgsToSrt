using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Nikse.SubtitleEdit.Core.Common;
using Nikse.SubtitleEdit.Core.SubtitleFormats;
using PgsToSrt.BluRaySup;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using TesseractOCR;
using TesseractOCR.Enums;

namespace PgsToSrt;

public class PgsOcr
{
    private readonly Microsoft.Extensions.Logging.ILogger _logger;
    private readonly string _tesseractVersion;
    private readonly string _libLeptName;
    private readonly string _libLeptVersion;

    public string TesseractDataPath { get; set; }
    public string TesseractLanguage { get; set; } = "eng";
    public string CharacterBlacklist { get; set; }
    public int ShortThreshold { get; set; } = 300;
    public int ExtendTo { get; set; } = 1200;

    public PgsOcr(Microsoft.Extensions.Logging.ILogger logger, string tesseractVersion, string libLeptName, string libLeptVersion)
    {
        _logger = logger;
        _tesseractVersion = tesseractVersion;
        _libLeptName = libLeptName;
        _libLeptVersion = libLeptVersion;
    }

    public bool ToSrt(List<BluRaySupParserImageSharp.PcsData> subtitles, string outputFileName)
    {
        if (subtitles == null || subtitles.Count == 0)
        {
            _logger.LogWarning("No subtitles to process");
            return false;
        }

        _logger.LogInformation($"Starting OCR for {subtitles.Count} subtitles...");
        _logger.LogInformation($"Tesseract version: {_tesseractVersion}");

        if (!string.IsNullOrEmpty(CharacterBlacklist))
        {
            _logger.LogInformation($"Character blacklist: '{CharacterBlacklist}'");
        }

        if (ShortThreshold > 0 && ExtendTo > 0)
        {
            _logger.LogInformation($"Short subtitle extension: subtitles < {ShortThreshold}ms will be extended to {ExtendTo}ms");
        }

        var initException = TesseractApi.Initialize(_tesseractVersion, _libLeptName, _libLeptVersion);
        if (initException != null)
        {
            _logger.LogError(initException, "Failed to initialize Tesseract");
            return false;
        }

        try
        {
            var paragraphs = ProcessSubtitles(subtitles);
            var subtitle = CreateSubtitle(paragraphs);
            SaveSubtitle(subtitle, outputFileName);
            
            _logger.LogInformation($"Successfully saved '{outputFileName}' with {subtitle.Paragraphs.Count} subtitles");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to process subtitles: {ex.Message}");
            return false;
        }
    }

    private List<Paragraph> ProcessSubtitles(List<BluRaySupParserImageSharp.PcsData> subtitles)
    {
        var ocrResults = new ConcurrentBag<OcrResult>();
        var processedCount = 0;

        // Extract text using simple OCR approach
        Parallel.ForEach(Enumerable.Range(0, subtitles.Count), i =>
        {
            try
            {
                using var engine = new Engine(TesseractDataPath, TesseractLanguage);
                ConfigureEngine(engine);
                
                var item = subtitles[i];
                var text = ExtractText(engine, item);
                
                if (!string.IsNullOrWhiteSpace(text))
                {
                    ocrResults.Add(new OcrResult
                    {
                        Index = i,
                        Text = text,
                        StartTime = item.StartTime,
                        EndTime = item.EndTime
                    });
                }

                var count = Interlocked.Increment(ref processedCount);
                if (count % 50 == 0)
                {
                    _logger.LogInformation($"Processed {count}/{subtitles.Count} subtitles");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Failed to process subtitle {i}: {ex.Message}");
            }
        });

        _logger.LogInformation($"OCR completed. Found {ocrResults.Count} valid subtitles from {subtitles.Count} processed");

        var sortedResults = ocrResults.OrderBy(r => r.Index).ToList();
        
        // Group overlapping subtitles to combine simultaneous dialogue
        var groupedResults = GroupOverlappingSubtitles(sortedResults);
        
        // Convert to paragraphs with duration extension
        var paragraphs = ConvertToParagraphs(groupedResults);
        
        return RemoveDuplicates(paragraphs);
    }

    private string ExtractText(Engine engine, BluRaySupParserImageSharp.PcsData item)
    {
        try
        {
            using var bitmap = GetSubtitleBitmap(item);
            if (bitmap == null || bitmap.Width == 0 || bitmap.Height == 0)
            {
                return string.Empty;
            }

            using var image = ConvertToPix(bitmap);
            using var page = engine.Process(image, PageSegMode.Auto);
            
            return page.Text?.Trim() ?? string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogDebug($"OCR failed: {ex.Message}");
            return string.Empty;
        }
    }

    private void ConfigureEngine(Engine engine)
    {
        try
        {
            if (!string.IsNullOrEmpty(CharacterBlacklist))
            {
                engine.SetVariable("tessedit_char_blacklist", CharacterBlacklist);
            }

            engine.SetVariable("tessedit_create_hocr", "0");
            engine.SetVariable("tessedit_create_pdf", "0");
            engine.SetVariable("tessedit_write_images", "0");
            engine.SetVariable("classify_enable_learning", "0");
            engine.SetVariable("classify_enable_adaptive_matcher", "1");
            engine.SetVariable("textord_really_old_xheight", "1");
            engine.SetVariable("language_model_penalty_non_dict_word", "0.8");
            engine.SetVariable("language_model_penalty_non_freq_dict_word", "0.9");
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"Failed to configure Tesseract engine: {ex.Message}");
        }
    }

    private static TesseractOCR.Pix.Image ConvertToPix(Image<Rgba32> bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.SaveAsBmp(stream);
        return TesseractOCR.Pix.Image.LoadFromMemory(stream.ToArray());
    }

    private Image<Rgba32> GetSubtitleBitmap(BluRaySupParserImageSharp.PcsData item)
    {
        try
        {
            return item.GetRgba32();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get bitmap from subtitle");
            return null;
        }
    }

    private List<OcrResult> GroupOverlappingSubtitles(List<OcrResult> results)
    {
        if (results.Count <= 1) return results;

        var grouped = new List<OcrResult>();
        var processed = new HashSet<int>();

        for (int i = 0; i < results.Count; i++)
        {
            if (processed.Contains(i)) continue;

            var current = results[i];
            var overlappingGroup = new List<OcrResult> { current };
            processed.Add(i);

            var currentStartMs = current.StartTime / 90.0;
            var currentEndMs = current.EndTime / 90.0;

            // Find all subtitles that overlap with current subtitle's time range
            for (int j = i + 1; j < results.Count; j++)
            {
                if (processed.Contains(j)) continue;

                var other = results[j];
                var otherStartMs = other.StartTime / 90.0;
                var otherEndMs = other.EndTime / 90.0;

                // Check for time overlap
                bool overlaps = (otherStartMs < currentEndMs) && (otherEndMs > currentStartMs);

                if (overlaps)
                {
                    overlappingGroup.Add(other);
                    processed.Add(j);
                    
                    // Expand time range to include this subtitle
                    currentStartMs = Math.Min(currentStartMs, otherStartMs);
                    currentEndMs = Math.Max(currentEndMs, otherEndMs);
                }
            }

            // Combine overlapping subtitles
            if (overlappingGroup.Count > 1)
            {
                var combinedText = CombineOverlappingTexts(overlappingGroup);
                var earliestStart = (long)(currentStartMs * 90.0);
                var latestEnd = (long)(currentEndMs * 90.0);

                grouped.Add(new OcrResult
                {
                    Index = current.Index,
                    Text = combinedText,
                    StartTime = earliestStart,
                    EndTime = latestEnd
                });

                _logger.LogDebug($"Combined {overlappingGroup.Count} overlapping subtitles into one");
            }
            else
            {
                grouped.Add(current);
            }
        }

        _logger.LogInformation($"Grouped {results.Count} subtitles into {grouped.Count} (combined {results.Count - grouped.Count} overlapping)");
        return grouped;
    }

    private string CombineOverlappingTexts(List<OcrResult> overlappingSubtitles)
    {
        var uniqueLines = new List<string>();
        
        foreach (var subtitle in overlappingSubtitles)
        {
            if (string.IsNullOrWhiteSpace(subtitle.Text)) continue;
            
            var lines = subtitle.Text.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                                   .Select(l => l.Trim())
                                   .Where(l => !string.IsNullOrEmpty(l));
            
            foreach (var line in lines)
            {
                if (!IsLineDuplicate(line, uniqueLines))
                {
                    uniqueLines.Add(line);
                }
            }
        }
        
        return string.Join("\n", uniqueLines);
    }

    private static bool IsLineDuplicate(string newLine, List<string> existingLines)
    {
        foreach (var existing in existingLines)
        {
            if (string.Equals(existing, newLine, StringComparison.OrdinalIgnoreCase))
                return true;

            var normalizedExisting = NormalizeText(existing);
            var normalizedNew = NormalizeText(newLine);
            
            if (string.Equals(normalizedExisting, normalizedNew, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private List<Paragraph> ConvertToParagraphs(List<OcrResult> results)
    {
        var paragraphs = new List<Paragraph>();
        
        for (int i = 0; i < results.Count; i++)
        {
            var result = results[i];
            var startTime = new TimeCode(result.StartTime / 90.0);
            var endTime = new TimeCode(result.EndTime / 90.0);
            
            var currentDuration = endTime.TotalMilliseconds - startTime.TotalMilliseconds;
            var minDuration = CalculateMinimumDuration(result.Text);
            
            // Extend very short durations for readability
            if (currentDuration < minDuration)
            {
                var newEndTime = startTime.TotalMilliseconds + minDuration;
                
                // Check if extending would overlap with next subtitle
                if (i + 1 < results.Count)
                {
                    var nextStartTime = new TimeCode(results[i + 1].StartTime / 90.0).TotalMilliseconds;
                    if (newEndTime > nextStartTime - 100)
                    {
                        newEndTime = Math.Max(nextStartTime - 100, startTime.TotalMilliseconds + 500);
                    }
                }
                
                if (newEndTime > startTime.TotalMilliseconds)
                {
                    endTime = new TimeCode(newEndTime);
                    _logger.LogDebug($"Extended subtitle {i + 1} from {currentDuration}ms to {endTime.TotalMilliseconds - startTime.TotalMilliseconds}ms for readability");
                }
            }
            
            paragraphs.Add(new Paragraph
            {
                Number = i + 1,
                StartTime = startTime,
                EndTime = endTime,
                Text = result.Text
            });
        }
        
        return paragraphs;
    }

    private int CalculateMinimumDuration(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 1000;

        var wordCount = text.Split(new[] { ' ', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).Length;
        var calculatedTime = Math.Max(wordCount * 250 + 500, 1000);
        
        if (ShortThreshold > 0)
            calculatedTime = Math.Max(calculatedTime, ShortThreshold);
            
        if (ExtendTo > 0)
            calculatedTime = Math.Max(calculatedTime, ExtendTo);
            
        return calculatedTime;
    }

    private List<Paragraph> RemoveDuplicates(List<Paragraph> paragraphs)
    {
        if (paragraphs.Count <= 1)
            return paragraphs;

        var filtered = new List<Paragraph>();
        var seenTexts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var paragraph in paragraphs)
        {
            var normalizedText = NormalizeText(paragraph.Text);
            
            if (!seenTexts.Contains(normalizedText))
            {
                seenTexts.Add(normalizedText);
                filtered.Add(paragraph);
            }
            else
            {
                _logger.LogDebug($"Removed exact duplicate: '{paragraph.Text.Substring(0, Math.Min(50, paragraph.Text.Length))}...'");
            }
        }

        _logger.LogInformation($"Removed {paragraphs.Count - filtered.Count} exact duplicate subtitles");

        // Renumber
        for (int i = 0; i < filtered.Count; i++)
        {
            filtered[i].Number = i + 1;
        }

        return filtered;
    }

    private static string NormalizeText(string text)
    {
        return System.Text.RegularExpressions.Regex.Replace(text.Trim(), @"\s+", " ");
    }

    private static Subtitle CreateSubtitle(List<Paragraph> paragraphs)
    {
        var subtitle = new Subtitle();
        subtitle.Paragraphs.AddRange(paragraphs);
        return subtitle;
    }

    private void SaveSubtitle(Subtitle subtitle, string outputFileName)
    {
        try
        {
            using var file = new StreamWriter(outputFileName, false, new UTF8Encoding(false));
            file.Write(subtitle.ToText(new SubRip()));
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to save subtitle file '{outputFileName}'", ex);
        }
    }

    private class OcrResult
    {
        public int Index { get; set; }
        public string Text { get; set; }
        public long StartTime { get; set; }
        public long EndTime { get; set; }
    }
}