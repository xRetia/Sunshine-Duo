// Simple JSON configuration next to the exe (GameManager.json):
//   LinksFolder     - folder scanned for .lnk game shortcuts
//   StartFullScreen - start borderless-fullscreen on launch (same as --fullscreen)

using System;
using System.IO;
using System.Text.Json;

namespace GameManager;

internal sealed class AppConfig
{
    public string LinksFolder { get; set; } = @"C:\MultiSeatGames\Links";

    public bool StartFullScreen { get; set; } = false;
}

internal static class Config
{
    private static readonly string ConfigPath =
        Path.Combine(AppContext.BaseDirectory, "GameManager.json");

    public static AppConfig Current { get; } = Load();

    private static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath));
                if (cfg != null) return cfg;
            }
        }
        catch
        {
            // Broken config: fall back to defaults below.
        }

        // First run (or unreadable config): write a default file so it is easy to edit.
        var defaults = new AppConfig();
        try
        {
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(defaults,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Read-only location: keep working with in-memory defaults.
        }
        return defaults;
    }
}