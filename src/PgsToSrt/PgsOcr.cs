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
                    _logger.LogInformation($"Processed item {paragraph.Number}.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error processing item {i}: {ex.Message}");
            }
        });

        // Sort the results and filter out empty entries
        var validResults = ocrResults
            .OrderBy(p => p.Number)
            .Where(p => !string.IsNullOrWhiteSpace(p.Text))
            .ToList();

        _subtitle.Paragraphs.AddRange(validResults);

        _logger.LogInformation($"Finished OCR. Kept {validResults.Count} valid entries out of {ocrResults.Count} processed.");
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
            // Method 1: Original approach with some improvements
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
            // Method 2: Enhanced preprocessing + different settings
            using var processedBitmap = PreprocessImage(bitmap);
            using var engine = new Engine(TesseractDataPath, TesseractLanguage);
            
            // More restrictive settings for cleaner text
            engine.SetVariable("tessedit_char_whitelist", "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789 .,!?'-:;()[]{}\"");
            engine.SetVariable("tessedit_pageseg_mode", "6"); // Single uniform block
            
            if (!string.IsNullOrEmpty(CharacterBlacklist))
                engine.SetVariable("tessedit_char_blacklist", CharacterBlacklist);
            
            using var image = GetPix(processedBitmap);
            using var page = engine.Process(image, PageSegMode.SingleBlock);
            
            return page.Text?.Trim() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static Image<Rgba32> PreprocessImage(Image<Rgba32> original)
    {
        var processed = original.Clone();
        
        processed.Mutate(x => x
            .Grayscale()           // Convert to grayscale
            .Contrast(1.5f)        // Increase contrast
            .GaussianSharpen(0.5f) // Sharpen slightly
            .Resize(original.Width * 2, original.Height * 2) // Scale up 2x
        );
        
        return processed;
    }

    private string ChooseBestResult(string result1, string result2)
    {
        // If one is empty, return the other
        if (string.IsNullOrWhiteSpace(result1)) return result2 ?? string.Empty;
        if (string.IsNullOrWhiteSpace(result2)) return result1;

        // Prefer longer results (usually more complete)
        if (result2.Length > result1.Length * 1.3) return result2;
        if (result1.Length > result2.Length * 1.3) return result1;

        // Prefer results with fewer artifacts
        int artifacts1 = CountArtifacts(result1);
        int artifacts2 = CountArtifacts(result2);
        
        if (artifacts1 < artifacts2) return result1;
        if (artifacts2 < artifacts1) return result2;

        // Default to first result
        return result1;
    }

    private static int CountArtifacts(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        
        int count = 0;
        foreach (char c in text)
        {
            if (c == '|' || c == '~' || c == '^' || c == '*' || c == '#' || 
                c == '%' || c == '&' || c == '@' || c == '}' || c == '{' || 
                c == ']' || c == '[')
            {
                count++;
            }
        }
        return count;
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