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
            var subtitle = new Subtitle();
            subtitle.Paragraphs.AddRange(paragraphs);
            
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

        // OCR all subtitles first
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

        _logger.LogInformation($"OCR completed. Found {ocrResults.Count} valid subtitles");

        // Sort by index
        var sortedResults = ocrResults.OrderBy(r => r.Index).ToList();
        
        // Merge consecutive duplicates
        var mergedResults = MergeConsecutiveDuplicates(sortedResults);
        
        // Convert to paragraphs
        return ConvertToParagraphs(mergedResults);
    }

    private List<OcrResult> MergeConsecutiveDuplicates(List<OcrResult> results)
    {
        if (results.Count <= 1)
            return results;
        
        var merged = new List<OcrResult>();
        var i = 0;
        
        while (i < results.Count)
        {
            var current = results[i];
            var startTime = current.StartTime;
            var endTime = current.EndTime;
            
            // Split current text into lines
            var currentLines = current.Text.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                                          .Select(l => l.Trim())
                                          .Where(l => !string.IsNullOrEmpty(l))
                                          .ToList();
            
            // Look ahead for entries that contain any of our lines
            var j = i + 1;
            while (j < results.Count && currentLines.Count < 3) // Limit to 3 lines max
            {
                var next = results[j];
                var nextLines = next.Text.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                                         .Select(l => l.Trim())
                                         .Where(l => !string.IsNullOrEmpty(l))
                                         .ToList();
                
                // Check if any line from current appears in next
                var hasOverlap = currentLines.Any(line => nextLines.Contains(line, StringComparer.OrdinalIgnoreCase));
                
                if (hasOverlap)
                {
                    // Only merge if we won't exceed 3 lines
                    var newLinesCount = nextLines.Count(line => !currentLines.Contains(line, StringComparer.OrdinalIgnoreCase));
                    if (currentLines.Count + newLinesCount <= 3)
                    {
                        // Extend time range
                        endTime = Math.Max(endTime, next.EndTime);
                        
                        // Add any new lines from next that aren't in current
                        foreach (var line in nextLines)
                        {
                            if (!currentLines.Contains(line, StringComparer.OrdinalIgnoreCase))
                            {
                                currentLines.Add(line);
                            }
                        }
                        
                        j++;
                    }
                    else
                    {
                        // Would exceed line limit, stop merging
                        break;
                    }
                }
                else
                {
                    break;
                }
            }
            
            // Create merged result
            merged.Add(new OcrResult
            {
                Index = current.Index,
                Text = string.Join("\n", currentLines.Take(3)), // Ensure max 3 lines
                StartTime = startTime,
                EndTime = endTime
            });
            
            i = j;
        }
        
        _logger.LogInformation($"Merged {results.Count} entries into {merged.Count} unique subtitles");
        return merged;
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