using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using pdfSignr.Services;

namespace pdfSignr.ViewModels;

// PDF compression and rasterization
public partial class MainViewModel
{
    [ObservableProperty] private bool _isCompressing;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task CompressResample(string presetName)
    {
        if (!Enum.TryParse<CompressionPreset>(presetName, out var preset)) return;
        await RunCompress(preset, rasterize: false);
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task CompressRasterize(string presetName)
    {
        if (!Enum.TryParse<CompressionPreset>(presetName, out var preset)) return;
        await RunCompress(preset, rasterize: true);
    }

    private async Task RunCompress(CompressionPreset preset, bool rasterize)
    {
        if (Pages.Count == 0) return;

        var suffix = rasterize ? "_rasterized" : "_compressed";
        var defaultName = PdfFilePath != null
            ? Path.GetFileNameWithoutExtension(PdfFilePath) + suffix
            : "document" + suffix;

        var (cancelled, outPw) = await GetOutputPasswordAsync();
        if (cancelled) return;

        var title = rasterize ? "Save Rasterized PDF" : "Save Compressed PDF";
        var outputPath = await _fileDialogs.PickSaveFileAsync(title, defaultName, ".pdf", ["*.pdf"]);
        if (outputPath == null) return;

        IsCompressing = true;
        var verb = rasterize ? "Rasterizing" : "Compressing";
        BaseStatus = $"{verb}\u2026";

        try
        {
            var pageSources = Pages.Select(p => (p.Source, p.RotationDegrees)).ToList();
            var progress = new Progress<int>(p =>
            {
                BaseStatus = $"{verb}\u2026 {p}%";
            });
            var result = rasterize
                ? await _compressService.RasterizeAsync(pageSources, outputPath, preset, outPw, progress)
                : await _compressService.CompressAsync(pageSources, outputPath, preset, outPw, progress);

            var saved = result.OriginalSize - result.CompressedSize;
            if (rasterize)
            {
                BaseStatus = $"Rasterized: {FormatSize(result.OriginalSize)} \u2192 {FormatSize(result.CompressedSize)}";
            }
            else
            {
                var imgNote = result.ImagesResampled > 0
                    ? $", {result.ImagesResampled} image{(result.ImagesResampled != 1 ? "s" : "")} resampled"
                    : ", no images to resample";
                BaseStatus = saved > 0
                    ? $"Compressed: {FormatSize(result.OriginalSize)} \u2192 {FormatSize(result.CompressedSize)} ({(double)saved / result.OriginalSize * 100:F0}% smaller{imgNote})"
                    : $"Compressed: {FormatSize(result.CompressedSize)} (no reduction{imgNote})";
            }
        }
        catch (Exception ex)
        {
            BaseStatus = $"{verb} failed: {ex.Message}";
            await _fileDialogs.ShowErrorAsync($"{verb} Failed", ex.Message);
        }
        finally
        {
            IsCompressing = false;
        }
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes / (1024.0 * 1024.0):F1} MB"
    };
}
