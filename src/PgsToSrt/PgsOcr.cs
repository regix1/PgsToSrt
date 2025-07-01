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
    public bool AllowOverlap { get; set; } = true;
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
        
        // Process overlapping subtitles intelligently if allowed
        var processedResults = AllowOverlap 
            ? ProcessOverlappingSubtitles(sortedResults)
            : MergeConsecutiveDuplicates(sortedResults);
        
        // Convert to paragraphs
        return ConvertToParagraphs(processedResults);
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
        
        // Group by Y position to identify different speakers
        var positionGroups = group
            .GroupBy(r => GetSpeakerGroup(r.YPosition))
            .OrderBy(g => g.Key)
            .ToList();
        
        var results = new List<OcrResult>();
        
        foreach (var posGroup in positionGroups)
        {
            var speakerSubs = posGroup.OrderBy(r => r.StartTime).ToList();
            
            // Try to merge consecutive identical/similar text from same speaker
            var merged = MergeSameSpeakerSubtitles(speakerSubs);
            results.AddRange(merged);
        }
        
        // Ensure proper timing for overlapping speakers
        results = AdjustOverlappingTiming(results);
        
        return results.OrderBy(r => r.StartTime).ThenBy(r => r.YPosition).ToList();
    }

    private int GetSpeakerGroup(int yPosition)
    {
        // Group Y positions into regions (top, middle, bottom)
        // This helps identify different speakers
        if (yPosition < 150)
            return 0; // Top speaker
        else if (yPosition < 350)
            return 1; // Middle speaker
        else
            return 2; // Bottom speaker
    }

    private List<OcrResult> MergeSameSpeakerSubtitles(List<OcrResult> speakerSubs)
    {
        if (speakerSubs.Count <= 1)
            return speakerSubs;
        
        var merged = new List<OcrResult>();
        var i = 0;
        
        while (i < speakerSubs.Count)
        {
            var current = speakerSubs[i];
            var currentLines = SplitIntoLines(current.Text);
            var startTime = current.StartTime;
            var endTime = current.EndTime;
            
            // Look ahead for continuations of the same dialogue
            var j = i + 1;
            while (j < speakerSubs.Count)
            {
                var next = speakerSubs[j];
                
                // Check if this is a continuation (small gap and similar position)
                var timeDiff = next.StartTime - endTime;
                var positionDiff = Math.Abs(next.YPosition - current.YPosition);
                
                if (timeDiff < 90 * 100 && positionDiff < PositionThreshold / 2) // Less than 100ms gap, similar position
                {
                    var nextLines = SplitIntoLines(next.Text);
                    
                    // Check if any lines are repeated (continuation of same dialogue)
                    var hasCommonLine = currentLines.Any(line => 
                        nextLines.Contains(line, StringComparer.OrdinalIgnoreCase));
                    
                    if (hasCommonLine || nextLines.All(line => 
                        !currentLines.Contains(line, StringComparer.OrdinalIgnoreCase)))
                    {
                        // Merge the subtitles
                        endTime = Math.Max(endTime, next.EndTime);
                        
                        // Add new lines that aren't already present
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
                Text = string.Join("\n", currentLines.Take(3)), // Max 3 lines
                StartTime = startTime,
                EndTime = endTime,
                YPosition = current.YPosition,
                IsForced = current.IsForced
            });
            
            i = j;
        }
        
        return merged;
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

    private List<OcrResult> AdjustOverlappingTiming(List<OcrResult> results)
    {
        // Don't cut off subtitles when they overlap
        // Instead, let them display simultaneously
        for (int i = 0; i < results.Count; i++)
        {
            var current = results[i];
            
            // Find all subtitles that overlap with this one
            var overlapping = results
                .Where((r, idx) => idx != i && 
                       SubtitlesOverlap(current.StartTime, current.EndTime, r.StartTime, r.EndTime))
                .ToList();
            
            if (overlapping.Any())
            {
                // Ensure minimum display time for readability
                var minDuration = CalculateMinimumDuration(current.Text);
                var currentDuration = current.EndTime - current.StartTime;
                
                if (currentDuration < minDuration * 90) // Convert to PTS units
                {
                    current.EndTime = current.StartTime + (minDuration * 90);
                }
            }
        }
        
        return results;
    }

    private bool SubtitlesOverlap(long start1, long end1, long start2, long end2)
    {
        return start1 < end2 && start2 < end1;
    }

    private List<string> SplitIntoLines(string text)
    {
        return text.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                   .Select(l => l.Trim())
                   .Where(l => !string.IsNullOrEmpty(l))
                   .ToList();
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
                
                if (AllowOverlap)
                {
                    // For overlapping subtitles, don't worry about extending into next subtitle
                    // This allows multiple speakers to be shown simultaneously
                    endTime = new TimeCode(newEndTime);
                }
                else
                {
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
                    }
                }
                
                _logger.LogDebug($"Extended subtitle {i + 1} from {currentDuration}ms to {endTime.TotalMilliseconds - startTime.TotalMilliseconds}ms for readability");
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
        public int YPosition { get; set; }
        public bool IsForced { get; set; }
    }
}