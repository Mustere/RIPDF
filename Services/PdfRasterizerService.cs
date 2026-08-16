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
        const byte whiteThreshold = 250;

        var pixelsCount = width * height;
        if (rawBytes.Length == 0 || pixelsCount <= 0)
        {
            return 0;
        }

        if (rawBytes.Length >= pixelsCount * 4)
        {
            return CalculateBgraFillPercentage(rawBytes, pixelsCount, whiteThreshold);
        }

        if (rawBytes.Length >= pixelsCount * 3)
        {
            return CalculateRgbFillPercentage(rawBytes, pixelsCount, whiteThreshold);
        }

        return CalculateGrayscaleFillPercentage(rawBytes, whiteThreshold);
    }

    private static double CalculateBgraFillPercentage(byte[] rawBytes, int pixelsCount, byte whiteThreshold)
    {
        long filledPixels = 0;
        var analyzedPixels = Math.Min(pixelsCount, rawBytes.Length / 4);

        for (var pixel = 0; pixel < analyzedPixels; pixel++)
        {
            var offset = pixel * 4;
            var b = rawBytes[offset];
            var g = rawBytes[offset + 1];
            var r = rawBytes[offset + 2];
            var a = rawBytes[offset + 3];

            if (a < byte.MaxValue)
            {
                r = CompositeOverWhite(r, a);
                g = CompositeOverWhite(g, a);
                b = CompositeOverWhite(b, a);
            }

            if (!IsWhite(r, g, b, whiteThreshold))
            {
                filledPixels++;
            }
        }

        return analyzedPixels == 0 ? 0 : filledPixels * 100.0 / analyzedPixels;
    }

    private static double CalculateRgbFillPercentage(byte[] rawBytes, int pixelsCount, byte whiteThreshold)
    {
        long filledPixels = 0;
        var analyzedPixels = Math.Min(pixelsCount, rawBytes.Length / 3);

        for (var pixel = 0; pixel < analyzedPixels; pixel++)
        {
            var offset = pixel * 3;
            var r = rawBytes[offset];
            var g = rawBytes[offset + 1];
            var b = rawBytes[offset + 2];

            if (!IsWhite(r, g, b, whiteThreshold))
            {
                filledPixels++;
            }
        }

        return analyzedPixels == 0 ? 0 : filledPixels * 100.0 / analyzedPixels;
    }

    private static double CalculateGrayscaleFillPercentage(byte[] rawBytes, byte whiteThreshold)
    {
        long filledPixels = 0;

        foreach (var value in rawBytes)
        {
            if (value < whiteThreshold)
            {
                filledPixels++;
            }
        }

        return filledPixels * 100.0 / rawBytes.Length;
    }

    private static bool IsWhite(byte r, byte g, byte b, byte whiteThreshold)
    {
        return r >= whiteThreshold && g >= whiteThreshold && b >= whiteThreshold;
    }

    private static byte CompositeOverWhite(byte color, byte alpha)
    {
        return (byte)Math.Round((color * alpha + byte.MaxValue * (byte.MaxValue - alpha)) / (double)byte.MaxValue);
    }

}

