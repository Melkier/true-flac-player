using System.Text.Json;

namespace TrueFlac.Core;

public sealed class AppPreferences
{
    public int MinimumSampleRate { get; set; } = 44100;
    public int MinimumBitsPerSample { get; set; } = 16;
    public bool RequireValidMd5 { get; set; } = true;
    public bool RejectSuspectedTranscodes { get; set; } = true;
    public bool PreferWasapiExclusive { get; set; } = true;
    public string? PreferredDeviceId { get; set; }
    public string DownloadDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Downloads",
        "TrueFlac-Descargas");
    public List<string> WatchedPaths { get; set; } = new();

    public QualityPolicy ToPolicy() => new()
    {
        MinimumSampleRate = MinimumSampleRate,
        MinimumBitsPerSample = MinimumBitsPerSample,
        RequireValidMd5 = RequireValidMd5,
        RejectSuspectedTranscodes = RejectSuspectedTranscodes
    };
}

public static class PreferencesStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static string DefaultConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TrueFlacPlayer",
        "preferences.json");

    public static AppPreferences Load(string? path = null)
    {
        string target = path ?? DefaultConfigPath;
        try
        {
            if (File.Exists(target))
            {
                string json = File.ReadAllText(target);
                return JsonSerializer.Deserialize<AppPreferences>(json) ?? new AppPreferences();
            }
        }
        catch { }
        return new AppPreferences();
    }

    public static void Save(AppPreferences prefs, string? path = null)
    {
        string target = path ?? DefaultConfigPath;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, JsonSerializer.Serialize(prefs, JsonOpts));
    }
}
