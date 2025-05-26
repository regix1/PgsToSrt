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
        var lockObject = new object();

        // Process subtitles in parallel
        Parallel.ForEach(Enumerable.Range(0, _bluraySubtitles.Count), i =>
        {
            try
            {
                var item = _bluraySubtitles[i];
                
                // Skip items with invalid timing
                if (item.StartTime >= item.EndTime)
                {
                    return;
                }

                var paragraph = new Paragraph
                {
                    Number = i + 1,
                    StartTime = new TimeCode(item.StartTime / 90.0),
                    EndTime = new TimeCode(item.EndTime / 90.0),
                    Text = GetText(i)
                };

                ocrResults.Add(paragraph);

                if (i % 50 == 0)
                {
                    lock (lockObject)
                    {
                        _logger.LogInformation($"Processed item {paragraph.Number}.");
                    }
                }
            }
            catch (Exception ex)
            {
                lock (lockObject)
                {
                    _logger.LogError(ex, $"Error processing item {i}: {ex.Message}");
                }
            }
        });

        // Sort the results and filter out empty/invalid entries
        var validResults = ocrResults
            .OrderBy(p => p.Number)
            .Where(p => !string.IsNullOrWhiteSpace(p.Text) && 
                       p.EndTime.TotalMilliseconds - p.StartTime.TotalMilliseconds > 100) // Minimum 100ms duration
            .ToList();

        _subtitle.Paragraphs.AddRange(validResults);

        _logger.LogInformation($"Finished OCR. Processed {ocrResults.Count} items, kept {validResults.Count} valid entries.");
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

            // Try two different OCR approaches
            var result1 = TryOcrMethod1(bitmap, index);
            var result2 = TryOcrMethod2(bitmap, index);

            // Pick the better result
            var bestResult = ChooseBestResult(result1, result2);
            
            return string.IsNullOrEmpty(bestResult) ? string.Empty : bestResult;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error in GetText for index {index}: {ex.Message}");
            return string.Empty;
        }
    }

    private string TryOcrMethod1(Image<Rgba32> bitmap, int index)
    {
        try
        {
            // Method 1: Preprocessed image with strict settings
            using var processedBitmap = PreprocessImageMethod1(bitmap);
            using var image = GetPix(processedBitmap);
            if (image == null) return string.Empty;

            using var engine = new Engine(TesseractDataPath, TesseractLanguage);
            
            // Strict OCR settings
            engine.SetVariable("tessedit_char_whitelist", "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789 .,!?'-:;()[]{}\"");
            engine.SetVariable("tessedit_pageseg_mode", "6"); // Single uniform block
            engine.SetVariable("tessedit_ocr_engine_mode", "1"); // LSTM only
            
            if (!string.IsNullOrEmpty(CharacterBlacklist))
                engine.SetVariable("tessedit_char_blacklist", CharacterBlacklist);
            
            using var page = engine.Process(image, PageSegMode.SingleBlock);
            return page.Text?.Trim() ?? string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogDebug($"OCR Method 1 failed for index {index}: {ex.Message}");
            return string.Empty;
        }
    }

    private string TryOcrMethod2(Image<Rgba32> bitmap, int index)
    {
        try
        {
            // Method 2: Different preprocessing with more flexible settings
            using var processedBitmap = PreprocessImageMethod2(bitmap);
            using var image = GetPix(processedBitmap);
            if (image == null) return string.Empty;

            using var engine = new Engine(TesseractDataPath, TesseractLanguage);
            
            // More flexible OCR settings
            engine.SetVariable("tessedit_pageseg_mode", "8"); // Single word
            engine.SetVariable("tessedit_ocr_engine_mode", "3"); // Default + LSTM
            
            if (!string.IsNullOrEmpty(CharacterBlacklist))
                engine.SetVariable("tessedit_char_blacklist", CharacterBlacklist);
            
            using var page = engine.Process(image, PageSegMode.SingleWord);
            return page.Text?.Trim() ?? string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogDebug($"OCR Method 2 failed for index {index}: {ex.Message}");
            return string.Empty;
        }
    }

    private static Image<Rgba32> PreprocessImageMethod1(Image<Rgba32> original)
    {
        var processed = original.Clone();
        
        processed.Mutate(x => x
            // Convert to grayscale for better OCR
            .Grayscale()
            // High contrast for clear text
            .Contrast(1.8f)
            // Sharpen for crisp edges
            .GaussianSharpen(0.8f)
            // Scale up significantly
            .Resize(original.Width * 3, original.Height * 3)
        );
        
        return processed;
    }

    private static Image<Rgba32> PreprocessImageMethod2(Image<Rgba32> original)
    {
        var processed = original.Clone();
        
        processed.Mutate(x => x
            // Keep color information initially
            .Contrast(1.3f)
            // Light blur to reduce noise
            .GaussianBlur(0.5f)
            // Then convert to grayscale
            .Grayscale()
            // Moderate scaling
            .Resize(original.Width * 2, original.Height * 2)
            // Final sharpening
            .GaussianSharpen(0.3f)
        );
        
        return processed;
    }

    private string ChooseBestResult(string result1, string result2)
    {
        // If one is empty, return the other
        if (string.IsNullOrWhiteSpace(result1)) return result2 ?? string.Empty;
        if (string.IsNullOrWhiteSpace(result2)) return result1;

        // Prefer longer results (usually more complete)
        if (result2.Length > result1.Length * 1.2) return result2;
        if (result1.Length > result2.Length * 1.2) return result1;

        // Prefer results with fewer OCR artifacts
        int artifacts1 = CountOcrArtifacts(result1);
        int artifacts2 = CountOcrArtifacts(result2);
        
        if (artifacts1 < artifacts2) return result1;
        if (artifacts2 < artifacts1) return result2;

        // Prefer results with more dictionary words (simple heuristic)
        int words1 = CountLikelyWords(result1);
        int words2 = CountLikelyWords(result2);
        
        if (words1 > words2) return result1;
        if (words2 > words1) return result2;

        // Default to first result
        return result1;
    }

    private static int CountOcrArtifacts(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        
        int count = 0;
        var artifacts = new[] { '|', '~', '^', '*', '#', '%', '&', '@', '}', '{', ']', '[' };
        
        foreach (char c in text)
        {
            foreach (var artifact in artifacts)
            {
                if (c == artifact)
                {
                    count++;
                    break;
                }
            }
        }
        
        return count;
    }

    private static int CountLikelyWords(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int wordCount = 0;
        
        foreach (var word in words)
        {
            // Simple heuristic: words with more vowels are more likely to be real
            var cleanWord = word.ToLower().Trim(".,!?;:()[]{}\"'-".ToCharArray());
            if (cleanWord.Length >= 2)
            {
                int vowelCount = 0;
                foreach (char c in cleanWord)
                {
                    if ("aeiou".Contains(c))
                        vowelCount++;
                }
                
                if (vowelCount > 0 && vowelCount <= cleanWord.Length * 0.7) // Reasonable vowel ratio
                {
                    wordCount++;
                }
            }
        }
        
        return wordCount;
    }

    private static TesseractOCR.Pix.Image GetPix(Image<Rgba32> bitmap)
    {
        try
        {
            byte[] bytes;
            using (var stream = new MemoryStream())
            {
                // Use PNG format to preserve quality better than BMP
                bitmap.SaveAsPng(stream);
                bytes = stream.ToArray();
            }
            return TesseractOCR.Pix.Image.LoadFromMemory(bytes);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to convert image to Pix format: {ex.Message}", ex);
        }
    }

    private Image<Rgba32> GetSubtitleBitmap(int index)
    {
        try
        {
            if (index < 0 || index >= _bluraySubtitles.Count)
            {
                return null;
            }

            var item = _bluraySubtitles[index];
            if (item?.PcsObjects == null || item.PcsObjects.Count == 0)
            {
                return null;
            }

            return item.GetRgba32();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error getting subtitle bitmap for index {index}: {ex.Message}");
            return null;
        }
    }
}