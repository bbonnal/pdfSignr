using Microsoft.Extensions.Logging.Abstractions;
using pdfSignr.Services;
using Xunit;

namespace pdfSignr.Tests;

public class SettingsServiceTests : IDisposable
{
    private readonly string _path;

    public SettingsServiceTests()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pdfSignr-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "settings.json");
    }

    public void Dispose()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (dir != null && Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch { /* best-effort */ }
    }

    [Fact]
    public void Defaults_are_loaded_when_no_file_exists()
    {
        var svc = new SettingsService(_path, NullLogger<SettingsService>.Instance);
        var s = svc.Current;

        Assert.Equal(50, s.UndoMaxDepth);
        Assert.Equal(1.1, s.ZoomFactor);
        Assert.Equal(1024, s.CompressDpi.ScreenMaxDim);
    }

    [Fact]
    public async Task Update_persists_and_reload_returns_same_values()
    {
        var svc = new SettingsService(_path, NullLogger<SettingsService>.Instance);
        svc.Update(s => s with { UndoMaxDepth = 7, ZoomFactor = 1.25 });
        await svc.SaveAsync();

        var svc2 = new SettingsService(_path, NullLogger<SettingsService>.Instance);
        Assert.Equal(7, svc2.Current.UndoMaxDepth);
        Assert.Equal(1.25, svc2.Current.ZoomFactor);
    }

    [Fact]
    public void Changed_event_fires_on_update()
    {
        var svc = new SettingsService(_path, NullLogger<SettingsService>.Instance);
        var fired = 0;
        svc.Changed += _ => fired++;

        svc.Update(s => s with { UndoMaxDepth = 10 });
        Assert.Equal(1, fired);
    }
}
