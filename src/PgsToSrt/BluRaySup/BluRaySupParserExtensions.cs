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

                if (pcsData.BitmapObjects == null || pcsData.BitmapObjects.Count == 0)
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

                if (r.IsEmpty || r.Width <= 0 || r.Height <= 0)
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
                        if (singleBmp != null && singleBmp.Width > 0 && singleBmp.Height > 0)
                        {
                            mergedBmp.Mutate(b => b.DrawImage(singleBmp, new Point(offset.X, offset.Y), 1.0f));
                        }
                    }
                    catch (Exception)
                    {
                        // Continue processing other objects if one fails
                    }
                }

                return mergedBmp;
            }
            catch (Exception)
            {
                return new Image<Rgba32>(1, 1);
            }
        }
    }

    static class SupDecoder
    {
        /// <summary>
        /// Decode caption from the input stream
        /// </summary>
        /// <returns>bitmap of the decoded caption</returns>
        public static Image<Rgba32> DecodeImage(
            BluRaySupParserImageSharp.PcsObject pcs,
            IList<BluRaySupParserImageSharp.OdsData> data,
            List<BluRaySupParserImageSharp.PaletteInfo> palettes)
        {
            try
            {
                if (pcs == null || data == null || data.Count == 0)
                    return new Image<Rgba32>(1, 1);

                var width = data[0].Size.Width;
                var height = data[0].Size.Height;
                if (width <= 0 || height <= 0 || data[0].Fragment.ImageBuffer.Length == 0)
                    return new Image<Rgba32>(1, 1);

                // Ensure reasonable size limits
                width = Math.Min(width, 4096);
                height = Math.Min(height, 2160);

                using var bmp = new Image<Rgba32>(width, height);

                if (!bmp.DangerousTryGetSinglePixelMemory(out var pixelMemory))
                    return new Image<Rgba32>(1, 1);

                var pixelSpan = pixelMemory.Span;
                var palette = BluRaySupParserImageSharp.DecodePalette(palettes);
                
                int num1 = 0;
                int num2 = 0;
                int num3 = 0;
                byte[] imageBuffer = data[0].Fragment.ImageBuffer;
                
                do
                {
                    if (num3 >= imageBuffer.Length) break;
                    
                    var color1 = imageBuffer[num3++] & byte.MaxValue;
                    if (color1 == 0 && num3 < imageBuffer.Length)
                    {
                        int num4 = imageBuffer[num3++] & byte.MaxValue;
                        if (num4 == 0)
                        {
                            num1 = num1 / width * width;
                            if (num2 < width)
                                num1 += width;
                            num2 = 0;
                        }
                        else if ((num4 & 192) == 64)
                        {
                            if (num3 < imageBuffer.Length)
                            {
                                int num5 = (num4 - 64 << 8) + ((int) imageBuffer[num3++] & (int) byte.MaxValue);
                                Color color2 = GetColorFromInt(palette.GetArgb(0));
                                for (int index = 0; index < num5; ++index)
                                {
                                    PutPixel(pixelSpan, num1++, color2);
                                    if (num1 >= pixelSpan.Length) break;
                                }
                                num2 += num5;
                            }
                        }
                        else if ((num4 & 192) == 128)
                        {
                            if (num3 < imageBuffer.Length)
                            {
                                int num6 = num4 - 128;
                                int index1 = imageBuffer[num3++] & byte.MaxValue;
                                Color color3 = GetColorFromInt(palette.GetArgb(index1));
                                for (int index2 = 0; index2 < num6; ++index2)
                                {
                                    PutPixel(pixelSpan, num1++, color3);
                                    if (num1 >= pixelSpan.Length) break;
                                }
                                num2 += num6;
                            }
                        }
                        else if ((num4 & 192) != 0)
                        {
                            if (num3 + 1 < imageBuffer.Length)
                            {
                                int num7 = num4 - 192 << 8;
                                int num9 = imageBuffer[num3++] & byte.MaxValue;
                                int num10 = num7 + num9;
                                int index5 = imageBuffer[num3++] & byte.MaxValue;
                                Color color4 = GetColorFromInt(palette.GetArgb(index5));
                                for (int index6 = 0; index6 < num10; ++index6)
                                {
                                    PutPixel(pixelSpan, num1++, color4);
                                    if (num1 >= pixelSpan.Length) break;
                                }
                                num2 += num10;
                            }
                        }
                        else
                        {
                            Color color5 = GetColorFromInt(palette.GetArgb(0));
                            for (int index = 0; index < num4; ++index)
                            {
                                PutPixel(pixelSpan, num1++, color5);
                                if (num1 >= pixelSpan.Length) break;
                            }
                            num2 += num4;
                        }
                    }
                    else
                    {
                        PutPixel(pixelSpan, num1++, color1, palette);
                        ++num2;
                    }
                } while (num3 < imageBuffer.Length && num1 < pixelSpan.Length);

                // Create output image with padding
                var paddedWidth = Math.Min(width + 50, 4096);
                var paddedHeight = Math.Min(height + 50, 2160);
                var bmp2 = new Image<Rgba32>(paddedWidth, paddedHeight);
                
                bmp2.Mutate(i => i.DrawImage(bmp, new Point(25, 25), 1f));

                return bmp2;
            }
            catch (Exception)
            {
                return new Image<Rgba32>(1, 1);
            }
        }

        private static void PutPixel(Span<Rgba32> bmp, int index, int color, BluRaySupPalette palette)
        {
            if (palette == null) return;
            var colorArgb = GetColorFromInt(palette.GetArgb(color));
            PutPixel(bmp, index, colorArgb);
        }

        private static void PutPixel(Span<Rgba32> bmp, int index, Rgba32 color)
        {
            // BOUNDS CHECK FIX: Only write pixel if index is valid
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