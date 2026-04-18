namespace pdfSignr.Models;

public record AppSettings
{
    public int SchemaVersion { get; init; } = 1;
    public int UndoMaxDepth { get; init; } = 50;
    public double ZoomFactor { get; init; } = 1.1;
    public double MinZoom { get; init; } = 0.1;
    public double MaxZoom { get; init; } = 5.0;
    public bool GridModeDefault { get; init; } = false;
    public CompressionPresetDpi CompressDpi { get; init; } = new();
    public Dictionary<string, string> KeyBindingOverrides { get; init; } = new();
}

public record CompressionPresetDpi
{
    public int ScreenMaxDim { get; init; } = 1024;
    public int EbookMaxDim { get; init; } = 1600;
    public int PrintMaxDim { get; init; } = 2400;
}
