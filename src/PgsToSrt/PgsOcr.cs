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

        if (!DoOcr())
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

    private bool DoOcr()
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

        // Use ParallelOptions to control degree of parallelism
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Environment.ProcessorCount
        };

        // Process subtitles in parallel with controlled concurrency
        Parallel.ForEach(Enumerable.Range(0, _bluraySubtitles.Count), parallelOptions, i =>
        {
            try
            {
                var item = _bluraySubtitles[i];
                
                // Skip items with invalid timing
                if (item.StartTime >= item.EndTime)
                {
                    _logger.LogWarning($"Skipping item {i + 1} with invalid timing");
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

            using var image = GetPix(bitmap);
            if (image == null)
            {
                return string.Empty;
            }

            // Create a new engine for each thread to avoid thread safety issues
            using var engine = new Engine(TesseractDataPath, TesseractLanguage);
            
            // Set character blacklist if specified
            if (!string.IsNullOrEmpty(CharacterBlacklist))
            {
                engine.SetVariable("tessedit_char_blacklist", CharacterBlacklist);
            }
            
            // Set additional variables for better OCR results
            engine.SetVariable("tessedit_pageseg_mode", ((int)PageSegMode.Auto).ToString());
            
            using var page = engine.Process(image, PageSegMode.Auto);
            var text = page.Text?.Trim();
            
            return string.IsNullOrEmpty(text) ? string.Empty : text;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error in GetText for index {index}: {ex.Message}");
            return string.Empty;
        }
    }

    private static TesseractOCR.Pix.Image GetPix(Image<Rgba32> bitmap)
    {
        try
        {
            byte[] bytes;
            using (var stream = new MemoryStream())
            {
                // Use PNG format to preserve transparency better than BMP
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
                _logger.LogWarning($"Invalid index {index} for subtitle bitmap");
                return null;
            }

            var item = _bluraySubtitles[index];
            if (item?.PcsObjects == null || item.PcsObjects.Count == 0)
            {
                _logger.LogWarning($"No PCS objects found for index {index}");
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