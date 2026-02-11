namespace RIPDF.Models;

public sealed record RasterizationOptions(
    string InputPath,
    string OutputPath,
    int Dpi,
    int JpegQuality
);
