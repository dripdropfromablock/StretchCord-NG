using System.IO;

namespace StretchCordNG.Models
{
    public enum StretchMode
    {
        /// <summary>Force the source to fill the stream window (4:3 → 16:9).</summary>
        Fill,
        /// <summary>Preserve the source aspect ratio, letterbox the rest.</summary>
        Fit
    }

    /// <summary>User preferences, persisted as JSON under %LOCALAPPDATA%\StretchCord.</summary>
    public sealed class AppSettings
    {
        // Video
        public StretchMode StretchMode { get; set; } = StretchMode.Fill;
        public bool ShowCursor { get; set; } = true;
        /// <summary>0 = unlimited.</summary>
        public int MaxFps { get; set; } = 60;
        public bool LockAspect { get; set; } = true;
        public bool AlwaysOnTop { get; set; }
        public double StreamWindowWidth { get; set; } = 1280;
        public double StreamWindowHeight { get; set; } = 720;
        /// <summary>
        /// Pin the streaming worker threads (video frame delivery, audio capture) to the
        /// CPU's efficiency cores so the game keeps every performance core. Ignored on
        /// uniform CPUs.
        /// </summary>
        public bool PreferEfficiencyCores { get; set; } = true;

        // Audio
        /// <summary>null = auto-route (recommended).</summary>
        public string? AudioOutputId { get; set; }
        /// <summary>Mute the game's own speakers when there is no spare output device.</summary>
        public bool AutoMuteGame { get; set; } = true;
        public int AudioLatencyMs { get; set; } = 45;

        // Session
        public string? LastProcessName { get; set; }

        /// <summary>
        /// Per-game manual pointer corrections, keyed by lower-cased process name (the
        /// serialiser rebuilds the dictionary, so the key case is normalised by hand).
        /// </summary>
        public Dictionary<string, PointerCalibration> PointerCalibrations { get; set; } = new();
    }

    public static class SettingsStore
    {
        private static readonly System.Text.Json.JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
            PropertyNameCaseInsensitive = true
        };

        public static string FilePath { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StretchCord", "settings.json");

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    return System.Text.Json.JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options) ?? new AppSettings();
            }
            catch { /* corrupt or unreadable file — fall back to defaults */ }

            return new AppSettings();
        }

        public static void Save(AppSettings settings)
        {
            try
            {
                var dir = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(FilePath, System.Text.Json.JsonSerializer.Serialize(settings, Options));
            }
            catch { /* never let persistence break the app */ }
        }
    }
}
