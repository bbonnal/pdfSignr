using Avalonia.Input;
using Microsoft.Extensions.Logging.Abstractions;
using pdfSignr.Services;
using Xunit;

namespace pdfSignr.Tests;

public class KeyBindingServiceTests
{
    private static IKeyBindingService NewService()
    {
        var path = Path.Combine(Path.GetTempPath(), "pdfSignr-kb-" + Guid.NewGuid().ToString("N") + ".json");
        return new KeyBindingService(new SettingsService(path, NullLogger<SettingsService>.Instance));
    }

    [Fact]
    public void Every_binding_has_a_unique_chord()
    {
        var svc = NewService();
        var duplicates = svc.Bindings
            .GroupBy(b => b.Chord)
            .Where(g => g.Count() > 1)
            .ToList();

        Assert.Empty(duplicates);
    }

    [Fact]
    public void Display_rows_cover_all_categories()
    {
        var svc = NewService();
        var categories = svc.DisplayRows.Select(r => r.Category).Distinct().ToList();

        Assert.Contains("File", categories);
        Assert.Contains("Selection", categories);
        Assert.Contains("Pages", categories);
        Assert.Contains("Tools", categories);
        Assert.Contains("View", categories);
        Assert.Contains("Navigation", categories);
    }

    [Fact]
    public void Undo_chord_resolves_to_a_known_binding()
    {
        var svc = NewService();
        var chord = new KeyChord(Key.Z, KeyModifiers.Control);
        var match = svc.Bindings.FirstOrDefault(b => b.Chord == chord);

        Assert.NotNull(match);
        Assert.Equal("undo", match.Id);
    }
}
