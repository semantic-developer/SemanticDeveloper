using System;
using System.Collections.Generic;
using System.IO;

namespace SemanticDeveloper.Services;

public static class CodexConfigService
{
    public static string GetConfigTomlPath()
    {
        try
        {
            var home = Environment.GetEnvironmentVariable("CODEX_HOME");
            string dir;
            if (!string.IsNullOrWhiteSpace(home)) dir = home!;
            else dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "config.toml");
        }
        catch
        {
            return Path.Combine(Directory.GetCurrentDirectory(), "config.toml");
        }
    }

    public static string GetPromptsDirectory()
    {
        try
        {
            var home = Environment.GetEnvironmentVariable("CODEX_HOME");
            string dir;
            if (!string.IsNullOrWhiteSpace(home)) dir = home!;
            else dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
            return Path.Combine(dir, "prompts");
        }
        catch
        {
            return Path.Combine(Directory.GetCurrentDirectory(), "prompts");
        }
    }

    public static List<string> TryGetProfiles()
    {
        var result = new List<string>();
        try
        {
            var path = GetConfigTomlPath();
            if (!File.Exists(path)) return result;
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.StartsWith("#") || line.Length == 0) continue;
                // [profiles.NAME]
                if (line.StartsWith("[profiles.") && line.EndsWith("]"))
                {
                    var inner = line.Substring("[profiles.".Length);
                    inner = inner.Substring(0, inner.Length - 1); // drop ']'
                    inner = inner.Trim();
                    if (inner.Length > 0 && !result.Exists(s => string.Equals(s, inner, StringComparison.OrdinalIgnoreCase)))
                        result.Add(inner);
                }
            }
            result.Sort(StringComparer.OrdinalIgnoreCase);
        }
        catch { }
        return result;
    }
}
