using System;
using System.Collections.Generic;
using System.IO;

namespace SemanticDeveloper.Services;

public static class CodexConfigService
{
    private static bool _useWsl;

    public static string? LastProfileSource { get; private set; }
    public static string? LastProfileError { get; private set; }

    public static void SetUseWsl(bool useWsl)
    {
        _useWsl = useWsl && OperatingSystem.IsWindows();
        WslInterop.SetUseWsl(_useWsl);
    }

    public static string GetConfigTomlPath()
    {
        try
        {
            if (_useWsl && WslInterop.IsEnabled)
            {
                var wslPath = WslInterop.TryConvertToWindowsPath("~/.codex/config.toml");
                if (!string.IsNullOrWhiteSpace(wslPath))
                {
                    Console.WriteLine($"[CodexConfig] Using WSL config path {wslPath}.");
                    LastProfileSource = $"wslpath:{wslPath}";
                    return wslPath;
                }

                Console.WriteLine("[CodexConfig] Could not convert WSL config path; falling back to Windows default.");
            }

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
            if (_useWsl && WslInterop.IsEnabled)
            {
                var wslPath = WslInterop.TryConvertToWindowsPath("~/.codex/prompts");
                if (!string.IsNullOrWhiteSpace(wslPath))
                {
                    Console.WriteLine($"[CodexConfig] Using WSL prompts path {wslPath}.");
                    return wslPath;
                }

                Console.WriteLine("[CodexConfig] Could not convert WSL prompts path; falling back to Windows default.");
            }

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
            LastProfileSource = null;
            LastProfileError = null;

            if (_useWsl && WslInterop.IsEnabled)
            {
                var content = WslInterop.ReadFile("~/.codex/config.toml");
                if (!string.IsNullOrWhiteSpace(content))
                {
                    Console.WriteLine("[CodexConfig] Loaded profiles from WSL config (~/.codex/config.toml).");
                    LastProfileSource = "wsl:~/.codex/config.toml";
                    return ParseProfiles(content);
                }

                if (string.IsNullOrWhiteSpace(content))
                {
                    var fallbackPath = WslInterop.TryConvertToWindowsPath("~/.codex/config.toml");
                    if (!string.IsNullOrWhiteSpace(fallbackPath) && File.Exists(fallbackPath))
                    {
                        Console.WriteLine($"[CodexConfig] WSL config not directly readable; using converted path {fallbackPath}.");
                        LastProfileSource = fallbackPath;
                        content = File.ReadAllText(fallbackPath);
                    }
                }
                if (!string.IsNullOrWhiteSpace(content))
                {
                    Console.WriteLine("[CodexConfig] Loaded profiles from converted WSL path.");
                    LastProfileSource = LastProfileSource ?? "wslpath (converted)";
                    return ParseProfiles(content);
                }
                LastProfileError = "WSL ~/.codex/config.toml not found";
                Console.WriteLine("[CodexConfig] Failed to load profiles from WSL; config file missing.");
                return result;
            }

            var path = GetConfigTomlPath();
            if (!File.Exists(path)) return result;
            var text = File.ReadAllText(path);
            Console.WriteLine($"[CodexConfig] Using Windows config path {path}.");
            LastProfileSource = path;
            result = ParseProfiles(text);
        }
        catch (Exception ex)
        {
            LastProfileError = ex.Message;
            Console.WriteLine($"[CodexConfig] Exception while loading profiles: {ex.Message}");
        }
        return result;
    }

    private static List<string> ParseProfiles(string? text)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
            return result;

        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("#") || trimmed.Length == 0) continue;
            if (trimmed.StartsWith("[profiles.") && trimmed.EndsWith("]"))
            {
                var inner = trimmed.Substring("[profiles.".Length);
                inner = inner.Substring(0, inner.Length - 1); // drop ']'
                inner = inner.Trim();
                if (inner.Length > 0 && !result.Exists(s => string.Equals(s, inner, StringComparison.OrdinalIgnoreCase)))
                    result.Add(inner);
            }
        }
        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }
}
