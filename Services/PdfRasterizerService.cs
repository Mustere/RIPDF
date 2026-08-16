using System.IO;

using Docnet.Core;
using Docnet.Core.Models;
using Docnet.Core.Readers;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using RIPDF.Models;
using SixLabors.ImageSharp;
using Image = SixLabors.ImageSharp.Image;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace RIPDF.Services;

public sealed class PdfRasterizerService
{
    public async Task RasterizeAsync(
        RasterizationOptions options,
        IProgress<string>? status = null,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await Task.Run(() => Execute(options, status, progress, cancellationToken), cancellationToken);
    }

    public async Task<Dictionary<int, double>> CheckFillAsync(
        string pdfPath,
        int dpi = 300,
        IProgress<string>? status = null,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return await Task.Run(() => AnalyzeFill(pdfPath, dpi, status, progress, cancellationToken), cancellationToken);
    }

    private static void Execute(
        RasterizationOptions options,
        IProgress<string>? status,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        status?.Report("Открытие PDF...");

        var tempFolder = Path.Combine(Path.GetTempPath(), $"RIPDF_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempFolder);
        var jpegPages = new List<string>();

        try
        {
            var firstDimensions = CalculateInitialDimensions(options.Dpi);
            using var docReader = DocLib.Instance.GetDocReader(options.InputPath, firstDimensions);
            var totalPages = docReader.GetPageCount();

            for (var index = 0; index < totalPages; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                status?.Report($"Растрирование страницы {index + 1}/{totalPages}...");

                using var pageReader = docReader.GetPageReader(index);
                var rawBytes = pageReader.GetImage();
                var width = pageReader.GetPageWidth();
                var height = pageReader.GetPageHeight();

                using var image = Image.LoadPixelData<Bgra32>(rawBytes, width, height);
                using var jpegImage = new Image<Rgb24>(width, height, SixLabors.ImageSharp.Color.White);
                jpegImage.Mutate(ctx => ctx.DrawImage(image, 1f));
                var jpegPath = Path.Combine(tempFolder, $"page_{index + 1:0000}.jpg");
                jpegImage.SaveAsJpeg(jpegPath, new JpegEncoder
                {
                    Quality = options.JpegQuality,
                    ColorType = JpegEncodingColor.YCbCrRatio420
                });

                jpegPages.Add(jpegPath);
                progress?.Report((index + 1d) / totalPages * 0.85);
            }

            cancellationToken.ThrowIfCancellationRequested();
            status?.Report("Сборка результирующего PDF...");

            using var resultDocument = new PdfDocument();
            foreach (var jpegPath in jpegPages)
            {
                cancellationToken.ThrowIfCancellationRequested();

                using var pageImage = XImage.FromFile(jpegPath);
                var pdfPage = resultDocument.AddPage();
                pdfPage.Width = PdfSharp.Drawing.XUnit.FromPoint(pageImage.PointWidth);
                pdfPage.Height = PdfSharp.Drawing.XUnit.FromPoint(pageImage.PointHeight);

                using var gfx = XGraphics.FromPdfPage(pdfPage);
                gfx.DrawImage(pageImage, 0, 0, pdfPage.Width.Point, pdfPage.Height.Point);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(options.OutputPath)!);
            resultDocument.Save(options.OutputPath);
            progress?.Report(1.0);
            status?.Report("Готово.");
        }
        finally
        {
            try
            {
                Directory.Delete(tempFolder, recursive: true);
            }
            catch
            {
                // Если временный каталог не удалился, продолжаем без ошибки.
            }
        }
    }

    private static PageDimensions CalculateInitialDimensions(int dpi)
    {
        const double widthInInches = 8.27;
        const double heightInInches = 11.69;

        var width = Math.Max(100, (int)Math.Round(widthInInches * dpi));
        var height = Math.Max(100, (int)Math.Round(heightInInches * dpi));
        return new PageDimensions(width, height);
    }

    private static Dictionary<int, double> AnalyzeFill(
        string pdfPath,
        int dpi,
        IProgress<string>? status,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var fillPercentages = new Dictionary<int, double>();
        status?.Report("Анализ заливки PDF...");

        try
        {
            var dimensions = CalculateInitialDimensions(dpi);
            using var docReader = DocLib.Instance.GetDocReader(pdfPath, dimensions);
            var totalPages = docReader.GetPageCount();

            for (var index = 0; index < totalPages; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                status?.Report($"Анализ заливки страницы {index + 1}/{totalPages}...");

                using var pageReader = docReader.GetPageReader(index);
                var rawBytes = pageReader.GetImage();
                var width = pageReader.GetPageWidth();
                var height = pageReader.GetPageHeight();

                var fillPercentage = CalculateFillPercentageFromBytes(rawBytes, width, height);
                fillPercentages[index + 1] = fillPercentage;

                progress?.Report((index + 1d) / totalPages);
            }

            status?.Report("Анализ заливки завершён.");
        }
        catch (Exception ex)
        {
            status?.Report($"Ошибка при анализе заливки: {ex.Message}");
        }

        return fillPercentages;
    }

    private static double CalculateFillPercentageFromBytes(byte[] rawBytes, int width, int height)
    {
        const byte whiteThreshold = 100; // Очень низкий порог для белого
        long whitePixels = 0;
        var pixelsCount = width * height;
        
        byte minVal = 255, maxVal = 0;
        
        // Проверяем разные форматы пикселей
        // Формат 1: BGRA (4 байта на пиксель)
        if (rawBytes.Length >= pixelsCount * 4)
        {
            for (var i = 0; i < rawBytes.Length - 3; i += 4)
            {
                var b = rawBytes[i];
                var g = rawBytes[i + 1];
                var r = rawBytes[i + 2];
                
                minVal = Math.Min(minVal, Math.Min(r, Math.Min(g, b)));
                maxVal = Math.Max(maxVal, Math.Max(r, Math.Max(g, b)));
                
                if (r >= whiteThreshold && g >= whiteThreshold && b >= whiteThreshold)
                {
                    whitePixels++;
                }
            }
            
            var analyzedPixels = rawBytes.Length / 4;
            if (analyzedPixels > 0)
            {
                var whitePercentage = (whitePixels * 100.0) / analyzedPixels;
                var fillPercentage = 100.0 - whitePercentage;
                
                // Сохраним диагностику
                System.Diagnostics.Debug.WriteLine($"BGRA Format: Min={minVal}, Max={maxVal}, White%={whitePercentage:F2}%, Fill%={fillPercentage:F2}%");
                
                return fillPercentage;
            }
        }
        // Формат 2: RGBA (4 байта на пиксель, другой порядок)
        else if (rawBytes.Length >= pixelsCount * 4)
        {
            for (var i = 0; i < rawBytes.Length - 3; i += 4)
            {
                var r = rawBytes[i];
                var g = rawBytes[i + 1];
                var b = rawBytes[i + 2];
                
                minVal = Math.Min(minVal, Math.Min(r, Math.Min(g, b)));
                maxVal = Math.Max(maxVal, Math.Max(r, Math.Max(g, b)));
                
                if (r >= whiteThreshold && g >= whiteThreshold && b >= whiteThreshold)
                {
                    whitePixels++;
                }
            }
            
            var analyzedPixels = rawBytes.Length / 4;
            if (analyzedPixels > 0)
            {
                var whitePercentage = (whitePixels * 100.0) / analyzedPixels;
                var fillPercentage = 100.0 - whitePercentage;
                
                System.Diagnostics.Debug.WriteLine($"RGBA Format: Min={minVal}, Max={maxVal}, White%={whitePercentage:F2}%, Fill%={fillPercentage:F2}%");
                
                return fillPercentage;
            }
        }
        // Формат 3: RGB (3 байта на пиксель)
        else if (rawBytes.Length >= pixelsCount * 3)
        {
            for (var i = 0; i < rawBytes.Length - 2; i += 3)
            {
                var r = rawBytes[i];
                var g = rawBytes[i + 1];
                var b = rawBytes[i + 2];
                
                minVal = Math.Min(minVal, Math.Min(r, Math.Min(g, b)));
                maxVal = Math.Max(maxVal, Math.Max(r, Math.Max(g, b)));
                
                if (r >= whiteThreshold && g >= whiteThreshold && b >= whiteThreshold)
                {
                    whitePixels++;
                }
            }
            
            var analyzedPixels = rawBytes.Length / 3;
            if (analyzedPixels > 0)
            {
                var whitePercentage = (whitePixels * 100.0) / analyzedPixels;
                var fillPercentage = 100.0 - whitePercentage;
                
                System.Diagnostics.Debug.WriteLine($"RGB Format: Min={minVal}, Max={maxVal}, White%={whitePercentage:F2}%, Fill%={fillPercentage:F2}%");
                
                return fillPercentage;
            }
        }
        // Формат 4: Одиночные байты (8-битный цвет)
        else
        {
            for (var i = 0; i < rawBytes.Length; i++)
            {
                minVal = Math.Min(minVal, rawBytes[i]);
                maxVal = Math.Max(maxVal, rawBytes[i]);
                
                if (rawBytes[i] >= whiteThreshold)
                {
                    whitePixels++;
                }
            }
            
            if (rawBytes.Length > 0)
            {
                var whitePercentage = (whitePixels * 100.0) / rawBytes.Length;
                var fillPercentage = 100.0 - whitePercentage;
                
                System.Diagnostics.Debug.WriteLine($"8-bit Format: Min={minVal}, Max={maxVal}, White%={whitePercentage:F2}%, Fill%={fillPercentage:F2}%");
                
                return fillPercentage;
            }
        }

        return 0;
    }

}

