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
    public bool AllowOverlap { get; set; } = false; // Default to false for compatibility
    public int PositionThreshold { get; set; } = 50;

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

        if (AllowOverlap)
        {
            _logger.LogInformation($"Overlap handling enabled with position threshold: {PositionThreshold} pixels");
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
                    // Get position to help identify different speakers
                    var position = item.GetPosition();
                    
                    ocrResults.Add(new OcrResult
                    {
                        Index = i,
                        Text = text,
                        StartTime = item.StartTime,
                        EndTime = item.EndTime,
                        YPosition = position.Top,
                        IsForced = item.IsForced
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
        
        // Detect if we have potential narrator issues
        var hasNarratorIssues = DetectNarratorOverlap(sortedResults);
        
        // Only apply overlap processing if enabled AND we detected issues
        var processedResults = (AllowOverlap && hasNarratorIssues) 
            ? ProcessOverlappingSubtitles(sortedResults)
            : MergeConsecutiveDuplicates(sortedResults);
        
        // Convert to paragraphs
        return ConvertToParagraphs(processedResults);
    }

    private bool DetectNarratorOverlap(List<OcrResult> results)
    {
        // Look for patterns that indicate narrator overlap issues:
        // 1. Very long subtitles (> 10 seconds) that overlap with multiple shorter ones
        // 2. Subtitles that span across 3+ other subtitles
        
        for (int i = 0; i < results.Count; i++)
        {
            var current = results[i];
            var duration = (current.EndTime - current.StartTime) / 90.0; // Convert to milliseconds
            
            // Check if this is a potential narrator subtitle (long duration)
            if (duration > 10000) // 10 seconds
            {
                // Count how many subtitles overlap with this one
                var overlapCount = 0;
                for (int j = i + 1; j < results.Count && j < i + 10; j++)
                {
                    var next = results[j];
                    if (SubtitlesOverlap(current.StartTime, current.EndTime, next.StartTime, next.EndTime))
                    {
                        overlapCount++;
                    }
                }
                
                // If this subtitle overlaps with 3+ others, we likely have a narrator issue
                if (overlapCount >= 3)
                {
                    _logger.LogInformation($"Detected potential narrator overlap at subtitle {i} (duration: {duration}ms, overlaps: {overlapCount})");
                    return true;
                }
            }
        }
        
        return false;
    }

    private List<OcrResult> ProcessOverlappingSubtitles(List<OcrResult> results)
    {
        if (results.Count <= 1)
            return results;
        
        var processed = new List<OcrResult>();
        var i = 0;
        
        while (i < results.Count)
        {
            var current = results[i];
            var overlappingGroup = new List<OcrResult> { current };
            
            // Find all subtitles that overlap with the current time range
            var j = i + 1;
            while (j < results.Count)
            {
                var next = results[j];
                
                // Check if next subtitle overlaps with any in the current group
                var hasOverlap = overlappingGroup.Any(sub => 
                    SubtitlesOverlap(sub.StartTime, sub.EndTime, next.StartTime, next.EndTime));
                
                if (hasOverlap)
                {
                    overlappingGroup.Add(next);
                    j++;
                }
                else if (next.StartTime < overlappingGroup.Max(g => g.EndTime))
                {
                    // This subtitle starts before the group ends, include it
                    overlappingGroup.Add(next);
                    j++;
                }
                else
                {
                    break;
                }
            }
            
            // Process the overlapping group
            var groupResults = ProcessOverlappingGroup(overlappingGroup);
            processed.AddRange(groupResults);
            
            i = j;
        }
        
        _logger.LogInformation($"Processed {results.Count} entries into {processed.Count} subtitles");
        return processed;
    }

    private List<OcrResult> ProcessOverlappingGroup(List<OcrResult> group)
    {
        if (group.Count == 1)
            return group;
        
        // Identify potential narrator subtitles (long duration, specific Y positions)
        var results = new List<OcrResult>();
        
        foreach (var sub in group)
        {
            var duration = (sub.EndTime - sub.StartTime) / 90.0;
            
            // If this is a very long subtitle that overlaps with many others, 
            // it's likely a narrator - adjust its timing
            if (duration > 10000 && group.Count > 3)
            {
                // Find the next non-overlapping subtitle
                var nextNonOverlapping = group
                    .Where(s => s != sub && s.StartTime > sub.StartTime)
                    .OrderBy(s => s.StartTime)
                    .FirstOrDefault(s => !SubtitlesOverlap(sub.StartTime, sub.EndTime, s.StartTime, s.EndTime));
                
                if (nextNonOverlapping != null)
                {
                    // Trim the narrator subtitle to end just before the next dialogue
                    sub.EndTime = nextNonOverlapping.StartTime - 90; // 1ms gap
                }
            }
            
            results.Add(sub);
        }
        
        return results.OrderBy(r => r.StartTime).ThenBy(r => r.YPosition).ToList();
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
                EndTime = endTime,
                YPosition = current.YPosition,
                IsForced = current.IsForced
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
            
            // Only extend truly short subtitles
            if (currentDuration < ShortThreshold && ShortThreshold > 0 && ExtendTo > 0)
            {
                var newEndTime = startTime.TotalMilliseconds + ExtendTo;
                
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
        // Standard reading speed: ~250ms per word + 500ms base
        return Math.Max(wordCount * 250 + 500, 1000);
    }

    private bool SubtitlesOverlap(long start1, long end1, long start2, long end2)
    {
        return start1 < end2 && start2 < end1;
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
        public int YPosition { get; set; }
        public bool IsForced { get; set; }
    }
}