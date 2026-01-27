using System.Text.Json;

namespace GordonWorker.Services;

public class AppSettings
{
    public string ReportDayOfWeek { get; set; } = "Monday";
    public int ReportHour { get; set; } = 9;
    public decimal ActuarialAlpha { get; set; } = 0.15m; // EMA Weight
    public int AnalysisWindowDays { get; set; } = 90;
    public string OllamaBaseUrl { get; set; } = "http://host.docker.internal:11434";
    public string OllamaModelName { get; set; } = "deepseek-coder";
    public string SystemPersona { get; set; } = "Gordon"; // For future extensibility
}

public interface ISettingsService
{
    Task<AppSettings> GetSettingsAsync();
    Task UpdateSettingsAsync(AppSettings newSettings);
}

public class SettingsService : ISettingsService
{
    private readonly string _filePath = "app_data/settings.json";
    private AppSettings _currentSettings;

    public SettingsService()
    {
        // Ensure directory exists
        Directory.CreateDirectory("app_data");
        
        if (File.Exists(_filePath))
        {
            var json = File.ReadAllText(_filePath);
            _currentSettings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        else
        {
            _currentSettings = new AppSettings();
            SaveSettings();
        }
    }

    public Task<AppSettings> GetSettingsAsync()
    {
        return Task.FromResult(_currentSettings);
    }

    public Task UpdateSettingsAsync(AppSettings newSettings)
    {
        _currentSettings = newSettings;
        SaveSettings();
        return Task.CompletedTask;
    }

    private void SaveSettings()
    {
        var json = JsonSerializer.Serialize(_currentSettings, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_filePath, json);
    }
}
