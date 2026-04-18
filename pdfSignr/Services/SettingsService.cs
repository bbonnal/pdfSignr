using System.Text.Json;
using Microsoft.Extensions.Logging;
using pdfSignr.Models;

namespace pdfSignr.Services;

public class SettingsService : ISettingsService
{
    private readonly string _path;
    private readonly ILogger<SettingsService> _logger;
    private AppSettings _current;

    public AppSettings Current => _current;
    public event Action<AppSettings>? Changed;

    public SettingsService(ILogger<SettingsService> logger)
        : this(DefaultPath(), logger) { }

    /// <summary>Test-friendly ctor that takes an explicit storage path.</summary>
    public SettingsService(string path, ILogger<SettingsService> logger)
    {
        _logger = logger;
        _path = path;
        _current = Load();
    }

    public static string DefaultPath()
    {
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(baseDir, "pdfSignr", "settings.json");
    }

    private AppSettings Load()
    {
        try
        {
            if (!File.Exists(_path)) return new AppSettings();
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load settings from {Path}; using defaults", _path);
            return new AppSettings();
        }
    }

    public async Task SaveAsync()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var json = JsonSerializer.Serialize(_current, JsonOptions);
            await File.WriteAllTextAsync(_path, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save settings to {Path}", _path);
        }
    }

    public void Update(Func<AppSettings, AppSettings> mutate)
    {
        _current = mutate(_current);
        Changed?.Invoke(_current);
        _ = SaveAsync();
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };
}
