using System;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PgsToSrt.BluRaySup;

public static class ImageExtensions
{
    private static int GetAlpha(Image<Rgba32> image, int x, int y)
    {
        if (x < 0 || x >= image.Width || y < 0 || y >= image.Height)
            return 0;
        return image[x, y].A;
    }

    private static bool IsLineTransparent(this Image<Rgba32> image, int y)
    {
        if (y < 0 || y >= image.Height)
            return true;
            
        for (var x = 0; x < image.Width; ++x)
        {
            if (image[x, y].A != 0)
                return false;
        }
        return true;
    }

    private static bool IsVerticalLineTransparent(this Image<Rgba32> image, int x)
    {
        if (x < 0 || x >= image.Width)
            return true;
            
        for (var y = 0; y < image.Height; ++y)
        {
            if (GetAlpha(image, x, y) > 0)
                return false;
        }
        return true;
    }

    public static int GetNonTransparentHeight(this Image<Rgba32> image)
    {
        if (image.Height == 0) return 0;
        
        var num1 = 0;
        var num2 = 0;
        for (var y = 0; y < image.Height; ++y)
        {
            var flag = image.IsLineTransparent(y);
            if (num1 == y && flag)
                ++num1;
            else if (flag)
                ++num2;
            else
                num2 = 0;
        }

        return Math.Max(0, image.Height - num1 - num2);
    }

    public static int GetNonTransparentWidth(this Image<Rgba32> image)
    {
        if (image.Width == 0) return 0;
        
        var num1 = 0;
        var num2 = 0;
        for (var x = 0; x < image.Width; ++x)
        {
            var flag = image.IsVerticalLineTransparent(x);
            if (num1 == x && flag)
                ++num1;
            else if (flag)
                ++num2;
            else
                num2 = 0;
        }

        return Math.Max(0, image.Width - num1 - num2);
    }

    public static bool IsEqualTo(this Image<Rgba32> image, Image<Rgba32> image2)
    {
        if (image.Width != image2.Width || image.Height != image2.Height)
            return false;
        if (image.Width == 0 && image.Height == 0 && image2.Width == 0 && image2.Height == 0)
            return true;

        if (!image.DangerousTryGetSinglePixelMemory(out var pixelMemory0) ||
            !image2.DangerousTryGetSinglePixelMemory(out var pixelMemory1))
            return false;

        var pixelSpan0 = pixelMemory0.Span;
        var pixelSpan1 = pixelMemory1.Span;

        if (pixelSpan0.Length != pixelSpan1.Length)
            return false;

        for (int index = 0; index < pixelSpan0.Length; ++index)
        {
            if (pixelSpan0[index] != pixelSpan1[index])
                return false;
        }

        return true;
    }
}