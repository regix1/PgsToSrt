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
using SixLabors.ImageSharp.Processing;
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
    public int ShortThreshold { get; set; } = 300; // Default 300ms
    public int ExtendTo { get; set; } = 1200; // Default 1200ms

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

        // Initialize Tesseract
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
        var results = new ConcurrentBag<OcrResult>();
        var processedCount = 0;

        Parallel.ForEach(subtitles, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, 
            (subtitle, loop, index) =>
        {
            try
            {
                var text = ExtractText(subtitle);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    results.Add(new OcrResult
                    {
                        Index = (int)index,
                        Text = text,
                        StartTime = subtitle.StartTime,
                        EndTime = subtitle.EndTime
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
                _logger.LogWarning(ex, $"Failed to process subtitle {index}: {ex.Message}");
            }
        });

        _logger.LogInformation($"OCR completed. Found {results.Count} valid subtitles from {subtitles.Count} processed");

        var sortedResults = results.OrderBy(r => r.Index).ToList();
        var paragraphs = ConvertToParagraphs(sortedResults);
        
        return RemoveDuplicates(paragraphs);
    }

    private string ExtractText(BluRaySupParserImageSharp.PcsData subtitle)
    {
        using var bitmap = GetBitmap(subtitle);
        if (bitmap == null || bitmap.Width == 0 || bitmap.Height == 0)
        {
            return string.Empty;
        }

        // Try basic OCR first
        var basicResult = PerformOcr(bitmap, PageSegMode.Auto);
        if (IsGoodResult(basicResult))
        {
            return basicResult;
        }

        // Try with enhanced preprocessing
        using var processed = EnhanceImage(bitmap);
        var processedResult = PerformOcr(processed, PageSegMode.SingleBlock);
        
        return ChooseBestResult(basicResult, processedResult);
    }

    private Image<Rgba32> GetBitmap(BluRaySupParserImageSharp.PcsData subtitle)
    {
        try
        {
            return subtitle.GetRgba32();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get bitmap from subtitle");
            return null;
        }
    }

    private string PerformOcr(Image<Rgba32> image, PageSegMode pageSegMode)
    {
        try
        {
            using var engine = new Engine(TesseractDataPath, TesseractLanguage);
            ConfigureEngine(engine);
            
            using var pixImage = ConvertToPix(image);
            using var page = engine.Process(pixImage, pageSegMode);
            
            return page.Text?.Trim() ?? string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogDebug($"OCR failed with {pageSegMode}: {ex.Message}");
            return string.Empty;
        }
    }

    private void ConfigureEngine(Engine engine)
    {
        try
        {
            // Set character blacklist
            if (!string.IsNullOrEmpty(CharacterBlacklist))
            {
                engine.SetVariable("tessedit_char_blacklist", CharacterBlacklist);
            }

            // Better OCR configuration for subtitles
            engine.SetVariable("tessedit_create_hocr", "0");
            engine.SetVariable("tessedit_create_pdf", "0");
            engine.SetVariable("tessedit_write_images", "0");
            
            // Improve text recognition
            engine.SetVariable("classify_enable_learning", "0");
            engine.SetVariable("classify_enable_adaptive_matcher", "1");
            engine.SetVariable("textord_really_old_xheight", "1");
            
            // Reduce word penalties for better subtitle recognition
            engine.SetVariable("language_model_penalty_non_dict_word", "0.8");
            engine.SetVariable("language_model_penalty_non_freq_dict_word", "0.9");
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"Failed to configure Tesseract engine: {ex.Message}");
        }
    }

    private static TesseractOCR.Pix.Image ConvertToPix(Image<Rgba32> image)
    {
        using var stream = new MemoryStream();
        image.SaveAsBmp(stream);
        return TesseractOCR.Pix.Image.LoadFromMemory(stream.ToArray());
    }

    private static Image<Rgba32> EnhanceImage(Image<Rgba32> original)
    {
        var enhanced = original.Clone();
        
        try
        {
            enhanced.Mutate(x => x
                .Grayscale()
                .Resize(original.Width * 2, original.Height * 2, KnownResamplers.Lanczos3)
                .GaussianSharpen(0.8f)
                .Contrast(1.1f));
            
            return enhanced;
        }
        catch
        {
            enhanced?.Dispose();
            return original.Clone();
        }
    }

    private static bool IsGoodResult(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        // Consider it good if it's reasonably long and has mostly valid characters
        var letterCount = text.Count(char.IsLetter);
        var totalCount = text.Length;
        
        return text.Length >= 3 && (double)letterCount / totalCount >= 0.5;
    }

    private static string ChooseBestResult(string result1, string result2)
    {
        if (string.IsNullOrWhiteSpace(result1)) return result2 ?? string.Empty;
        if (string.IsNullOrWhiteSpace(result2)) return result1;

        var score1 = CalculateTextScore(result1);
        var score2 = CalculateTextScore(result2);

        return score2 > score1 ? result2 : result1;
    }

    private static int CalculateTextScore(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;

        var score = 0;
        
        // Length bonus (capped)
        score += Math.Min(text.Length * 2, 100);
        
        // Letter ratio bonus - higher weight for good text
        var letterCount = text.Count(char.IsLetter);
        var letterRatio = (double)letterCount / text.Length;
        score += (int)(letterRatio * 80);
        
        // Word count bonus
        var words = text.Split(new[] { ' ', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        score += Math.Min(words.Length * 12, 60);
        
        // Penalty for excessive special characters
        var badChars = text.Count(c => !char.IsLetterOrDigit(c) && !char.IsWhiteSpace(c) && 
                                      !".,!?'-:;()[]{}\"&$%@#*/+=<>".Contains(c));
        score -= badChars * 8;
        
        // Bonus for proper sentence structure
        if (char.IsUpper(text[0]))
            score += 15;
        
        return Math.Max(0, score);
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
            
            // Extend short subtitles if configured
            if (ShortThreshold > 0 && ExtendTo > 0 && currentDuration < ShortThreshold)
            {
                var newEndTime = startTime.TotalMilliseconds + ExtendTo;
                
                // Check if extending would overlap with next subtitle
                if (i + 1 < results.Count)
                {
                    var nextStartTime = new TimeCode(results[i + 1].StartTime / 90.0).TotalMilliseconds;
                    if (newEndTime > nextStartTime - 200) // Leave 200ms gap
                    {
                        newEndTime = nextStartTime - 200;
                    }
                }
                
                if (newEndTime > startTime.TotalMilliseconds)
                {
                    endTime = new TimeCode(newEndTime);
                    _logger.LogDebug($"Extended short subtitle {i + 1} from {currentDuration}ms to {endTime.TotalMilliseconds - startTime.TotalMilliseconds}ms");
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

    private List<Paragraph> RemoveDuplicates(List<Paragraph> paragraphs)
    {
        if (paragraphs.Count <= 1)
            return paragraphs;

        var filtered = new List<Paragraph>();
        var toSkip = new HashSet<int>();

        for (int i = 0; i < paragraphs.Count; i++)
        {
            if (toSkip.Contains(i))
                continue;

            var current = paragraphs[i];
            var duplicateFound = false;

            // Look ahead for duplicates within a reasonable time window
            for (int j = i + 1; j < Math.Min(i + 5, paragraphs.Count); j++)
            {
                if (toSkip.Contains(j))
                    continue;

                var next = paragraphs[j];
                
                // Skip if too far apart in time
                if (Math.Abs(next.StartTime.TotalMilliseconds - current.StartTime.TotalMilliseconds) > 10000)
                    break;

                if (AreDuplicates(current.Text, next.Text))
                {
                    // Keep the better one
                    if (current.Text.Length >= next.Text.Length)
                    {
                        toSkip.Add(j);
                    }
                    else
                    {
                        toSkip.Add(i);
                        duplicateFound = true;
                        break;
                    }
                }
            }

            if (!duplicateFound)
            {
                filtered.Add(current);
            }
        }

        _logger.LogInformation($"Removed {paragraphs.Count - filtered.Count} duplicate subtitles");

        // Renumber
        for (int i = 0; i < filtered.Count; i++)
        {
            filtered[i].Number = i + 1;
        }

        return filtered;
    }

    private static bool AreDuplicates(string text1, string text2)
    {
        if (string.IsNullOrWhiteSpace(text1) || string.IsNullOrWhiteSpace(text2))
            return false;

        var normalized1 = NormalizeText(text1);
        var normalized2 = NormalizeText(text2);

        // Exact match
        if (normalized1.Equals(normalized2, StringComparison.OrdinalIgnoreCase))
            return true;

        // One contains the other (for partial duplicates)
        if (normalized1.Length >= 10 && normalized2.Length >= 10)
        {
            var longer = normalized1.Length > normalized2.Length ? normalized1 : normalized2;
            var shorter = normalized1.Length > normalized2.Length ? normalized2 : normalized1;
            
            if (longer.Contains(shorter, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
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