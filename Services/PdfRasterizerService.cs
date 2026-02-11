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
                var jpegPath = Path.Combine(tempFolder, $"page_{index + 1:0000}.jpg");
                image.Save(jpegPath, new JpegEncoder
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
}
