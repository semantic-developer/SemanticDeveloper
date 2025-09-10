using System;
using System.IO;
using Newtonsoft.Json;
using SemanticDeveloper.Models;

namespace SemanticDeveloper.Services;

public static class SettingsService
{
    private static readonly string AppDirAppData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SemanticDeveloper");
    private static readonly string AppDirLocalData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SemanticDeveloper");
    private static readonly string FilePathApp = Path.Combine(AppDirAppData, "settings.json");
    private static readonly string FilePathLocal = Path.Combine(AppDirLocalData, "settings.json");
    private static string? _loadedPath;

    public static AppSettings Load()
    {
        try
        {
            // Prefer ApplicationData (~/.config on Linux), else LocalApplicationData (~/.local/share)
            string? path = null;
            if (File.Exists(FilePathApp)) path = FilePathApp;
            else if (File.Exists(FilePathLocal)) path = FilePathLocal;

            if (path != null)
            {
                var json = File.ReadAllText(path);
                var settings = JsonConvert.DeserializeObject<AppSettings>(json);
                if (settings != null)
                {
                    _loadedPath = path;
                    return settings;
                }
            }
        }
        catch { }
        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            var path = _loadedPath ?? FilePathApp;
            var dir = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(dir);
            var json = JsonConvert.SerializeObject(settings, Formatting.Indented);
            File.WriteAllText(path, json);
        }
        catch { }
    }
}
