using pdfSignr.Models;

namespace pdfSignr.Services;

public interface ISettingsService
{
    AppSettings Current { get; }
    Task SaveAsync();
    void Update(Func<AppSettings, AppSettings> mutate);
    event Action<AppSettings>? Changed;
}
