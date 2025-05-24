// Improved SupDecoder.cs with better error handling and validation
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System;
using System.Collections.Generic;
using Nikse.SubtitleEdit.Core.BluRaySup;

namespace PgsToSrt.BluRaySup
{
    public static class BluRaySupParserExtensions
    {
        public static Image<Rgba32> GetRgba32(this BluRaySupParserImageSharp.PcsData pcsData)
        {
            try
            {
                if (pcsData?.PcsObjects == null || pcsData.PcsObjects.Count == 0)
                    return new Image<Rgba32>(1, 1);

                if (pcsData.PcsObjects.Count == 1)
                    return SupDecoder.DecodeImage(pcsData.PcsObjects[0], pcsData.BitmapObjects[0], pcsData.PaletteInfos);

                var r = Rectangle.Empty;
                for (var ioIndex = 0; ioIndex < pcsData.PcsObjects.Count; ioIndex++)
                {
                    if (ioIndex >= pcsData.BitmapObjects.Count || pcsData.BitmapObjects[ioIndex].Count == 0)
                        continue;
                        
                    var ioRect = new Rectangle(pcsData.PcsObjects[ioIndex].Origin, pcsData.BitmapObjects[ioIndex][0].Size);
                    r = r.IsEmpty ? ioRect : Rectangle.Union(r, ioRect);
                }

                if (r.Width <= 0 || r.Height <= 0)
                    return new Image<Rgba32>(1, 1);

                var mergedBmp = new Image<Rgba32>(r.Width, r.Height);
                for (var ioIndex = 0; ioIndex < pcsData.PcsObjects.Count; ioIndex++)
                {
                    if (ioIndex >= pcsData.BitmapObjects.Count)
                        continue;
                        
                    var offset = pcsData.PcsObjects[ioIndex].Origin - new Size(r.Location);
                    try
                    {
                        using var singleBmp = SupDecoder.DecodeImage(pcsData.PcsObjects[ioIndex], pcsData.BitmapObjects[ioIndex], pcsData.PaletteInfos);
                        if (singleBmp.Width > 1 && singleBmp.Height > 1) // Skip 1x1 error images
                        {
                            mergedBmp.Mutate(b => b.DrawImage(singleBmp, new Point(offset.X, offset.Y), 1f));
                        }
                    }
                    catch
                    {
                        // Skip corrupted individual objects
                        continue;
                    }
                }

                return mergedBmp;
            }
            catch
            {
                return new Image<Rgba32>(1, 1);
            }
        }
    }

    static class SupDecoder
    {
        private const int MAX_IMAGE_DIMENSION = 4096; // Reasonable max for subtitle images
        
        public static Image<Rgba32> DecodeImage(
            BluRaySupParserImageSharp.PcsObject pcs,
            IList<BluRaySupParserImageSharp.OdsData> data,
            List<BluRaySupParserImageSharp.PaletteInfo> palettes)
        {
            if (pcs == null || data == null || data.Count == 0)
                return new Image<Rgba32>(1, 1);
                
            var width = data[0].Size.Width;
            var height = data[0].Size.Height;
            
            // Validate image dimensions
            if (width <= 0 || height <= 0 || 
                width > MAX_IMAGE_DIMENSION || height > MAX_IMAGE_DIMENSION ||
                data[0].Fragment?.ImageBuffer == null || 
                data[0].Fragment.ImageBuffer.Length == 0)
            {
                return new Image<Rgba32>(1, 1);
            }

            // Validate palette
            if (palettes == null || palettes.Count == 0)
                return new Image<Rgba32>(1, 1);

            try
            {
                using var bmp = new Image<Rgba32>(width, height);

                if (!bmp.DangerousTryGetSinglePixelMemory(out var pixelMemory))
                    return new Image<Rgba32>(1, 1);
                    
                var pixelSpan = pixelMemory.Span;
                var palette = BluRaySupParserImageSharp.DecodePalette(palettes);
                
                if (!DecodeImageBuffer(data[0].Fragment.ImageBuffer, pixelSpan, width, height, palette))
                    return new Image<Rgba32>(1, 1);

                // Create output image with padding
                var bmp2 = new Image<Rgba32>(width + 50, height + 50);
                bmp2.Mutate(i => i.DrawImage(bmp, new Point(25, 25), 1f));

                return bmp2;
            }
            catch
            {
                return new Image<Rgba32>(1, 1);
            }
        }

        private static bool DecodeImageBuffer(byte[] imageBuffer, Span<Rgba32> pixelSpan, int width, int height, BluRaySupPalette palette)
        {
            try
            {
                int pixelIndex = 0;
                int x = 0;
                int bufferIndex = 0;
                int totalPixels = width * height;

                while (bufferIndex < imageBuffer.Length && pixelIndex < totalPixels)
                {
                    var color1 = imageBuffer[bufferIndex++] & byte.MaxValue;
                    
                    if (color1 == 0 && bufferIndex < imageBuffer.Length)
                    {
                        int num4 = imageBuffer[bufferIndex++] & byte.MaxValue;
                        
                        if (num4 == 0)
                        {
                            // End of line
                            pixelIndex = (pixelIndex / width) * width;
                            if (x < width)
                                pixelIndex += width;
                            x = 0;
                        }
                        else if ((num4 & 192) == 64)
                        {
                            // Transparent run
                            if (bufferIndex < imageBuffer.Length)
                            {
                                int runLength = (num4 - 64 << 8) + (imageBuffer[bufferIndex++] & byte.MaxValue);
                                var color = GetColorFromInt(palette.GetArgb(0));
                                
                                for (int i = 0; i < runLength && pixelIndex < totalPixels; ++i)
                                    PutPixel(pixelSpan, pixelIndex++, color);
                                x += runLength;
                            }
                        }
                        else if ((num4 & 192) == 128)
                        {
                            // Color run
                            if (bufferIndex < imageBuffer.Length)
                            {
                                int runLength = num4 - 128;
                                int colorIndex = imageBuffer[bufferIndex++] & byte.MaxValue;
                                var color = GetColorFromInt(palette.GetArgb(colorIndex));
                                
                                for (int i = 0; i < runLength && pixelIndex < totalPixels; ++i)
                                    PutPixel(pixelSpan, pixelIndex++, color);
                                x += runLength;
                            }
                        }
                        else if ((num4 & 192) != 0)
                        {
                            // Long color run
                            if (bufferIndex + 1 < imageBuffer.Length)
                            {
                                int runLength = ((num4 - 192) << 8) + (imageBuffer[bufferIndex++] & byte.MaxValue);
                                int colorIndex = imageBuffer[bufferIndex++] & byte.MaxValue;
                                var color = GetColorFromInt(palette.GetArgb(colorIndex));
                                
                                for (int i = 0; i < runLength && pixelIndex < totalPixels; ++i)
                                    PutPixel(pixelSpan, pixelIndex++, color);
                                x += runLength;
                            }
                        }
                        else
                        {
                            // Short transparent run
                            var color = GetColorFromInt(palette.GetArgb(0));
                            for (int i = 0; i < num4 && pixelIndex < totalPixels; ++i)
                                PutPixel(pixelSpan, pixelIndex++, color);
                            x += num4;
                        }
                    }
                    else
                    {
                        // Single pixel
                        PutPixel(pixelSpan, pixelIndex++, color1, palette);
                        ++x;
                    }
                    
                    // Prevent infinite loops on corrupted data
                    if (x >= width * 2) // Allow some overflow but not infinite
                        break;
                }
                
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void PutPixel(Span<Rgba32> bmp, int index, int color, BluRaySupPalette palette)
        {
            if (palette != null)
            {
                var colorArgb = GetColorFromInt(palette.GetArgb(color));
                PutPixel(bmp, index, colorArgb);
            }
        }

        private static void PutPixel(Span<Rgba32> bmp, int index, Rgba32 color)
        {
            if (index >= 0 && index < bmp.Length && color.A > 0)
            {
                bmp[index] = color;
            }
        }

        private static Rgba32 GetColorFromInt(int number)
        {
            var values = BitConverter.GetBytes(number);
            if (!BitConverter.IsLittleEndian) Array.Reverse(values);

            var b = values[0];
            var g = values[1];
            var r = values[2];
            var a = values[3];

            return new Rgba32(r, g, b, a);
        }
    }
}

// Improved PgsOcr.cs with better error handling
public class PgsOcr
{
    private readonly Microsoft.Extensions.Logging.ILogger _logger;
    private readonly Subtitle _subtitle = new();
    private readonly string _tesseractVersion;
    private readonly string _libLeptName;
    private readonly string _libLeptVersion;
    private List<BluRaySupParserImageSharp.PcsData> _bluraySubtitles;

    public string TesseractDataPath { get; set; }
    public string TesseractLanguage { get; set; } = "eng";

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
                using var engine = new Engine(TesseractDataPath, TesseractLanguage);

                var item = _bluraySubtitles[i];

                var paragraph = new Paragraph
                {
                    Number = i + 1,
                    StartTime = new TimeCode(item.StartTime / 90.0),
                    EndTime = new TimeCode(item.EndTime / 90.0),
                    Text = GetText(engine, i)
                };

                // Only add non-empty subtitles
                if (!string.IsNullOrWhiteSpace(paragraph.Text))
                {
                    ocrResults.Add(paragraph);
                }

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

        // Sort the results and add them to the subtitle
        _subtitle.Paragraphs.AddRange(ocrResults.OrderBy(p => p.Number));

        _logger.LogInformation("Finished OCR.");
        return true;
    }

    private string GetText(Engine engine, int index)
    {
        try
        {
            using var bitmap = GetSubtitleBitmap(index);
            
            // Skip tiny images that are likely corrupt
            if (bitmap.Width <= 1 || bitmap.Height <= 1)
                return "";
                
            // Check if image is mostly transparent (likely empty subtitle)
            if (IsImageMostlyTransparent(bitmap))
                return "";

            using var image = GetPix(bitmap);
            using var page = engine.Process(image, PageSegMode.Auto);

            var text = page.Text?.Trim();
            
            // Filter out obviously corrupted OCR results
            if (string.IsNullOrEmpty(text) || IsLikelyCorruptedText(text))
                return "";
                
            return text;
        }
        catch
        {
            return "";
        }
    }

    private static bool IsImageMostlyTransparent(Image<Rgba32> image)
    {
        if (!image.DangerousTryGetSinglePixelMemory(out var pixelMemory))
            return true;
            
        var pixelSpan = pixelMemory.Span;
        int opaquePixels = 0;
        int totalPixels = pixelSpan.Length;
        
        for (int i = 0; i < totalPixels; i++)
        {
            if (pixelSpan[i].A > 0)
                opaquePixels++;
        }
        
        return (double)opaquePixels / totalPixels < 0.01; // Less than 1% opaque
    }

    private static bool IsLikelyCorruptedText(string text)
    {
        if (text.Length < 3)
            return false;
            
        // Check for too many non-letter characters
        int letterCount = 0;
        foreach (char c in text)
        {
            if (char.IsLetter(c))
                letterCount++;
        }
        
        double letterRatio = (double)letterCount / text.Length;
        return letterRatio < 0.3; // Less than 30% letters suggests corruption
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
            return _bluraySubtitles[index].GetRgba32();
        }
        catch
        {
            // Return empty bitmap for corrupted frames
            return new Image<Rgba32>(1, 1);
        }
    }
}
