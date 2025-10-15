using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace SemanticDeveloper.Services;

public static class CodexVersionService
{
    private static readonly Regex VersionRegex = new(@"\b(v?\d+(?:\.\d+){0,3})\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static string GetVersionFilePath()
    {
        try
        {
            if (WslInterop.IsEnabled)
            {
                var wslPath = WslInterop.TryConvertToWindowsPath("~/.codex/version.json");
                if (!string.IsNullOrWhiteSpace(wslPath))
                {
                    Console.WriteLine($"[CodexVersion] Using WSL version path {wslPath}.");
                    return wslPath;
                }
            }

            var home = Environment.GetEnvironmentVariable("CODEX_HOME");
            string dir;
            if (!string.IsNullOrWhiteSpace(home)) dir = home!;
            else dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
            return Path.Combine(dir, "version.json");
        }
        catch
        {
            return Path.Combine(Directory.GetCurrentDirectory(), "version.json");
        }
    }

    public static (bool Ok, string? Version, string? Error) TryReadLatestVersion()
    {
        try
        {
            string? text = null;
            if (WslInterop.IsEnabled)
            {
                text = WslInterop.ReadFile("~/.codex/version.json");
                if (string.IsNullOrWhiteSpace(text))
                {
                    var wslPath = WslInterop.TryConvertToWindowsPath("~/.codex/version.json");
                    if (!string.IsNullOrWhiteSpace(wslPath) && File.Exists(wslPath))
                    {
                        Console.WriteLine($"[CodexVersion] Using converted WSL version path {wslPath}.");
                        text = File.ReadAllText(wslPath);
                    }
                }
                if (string.IsNullOrWhiteSpace(text))
                {
                    Console.WriteLine("[CodexVersion] Version file not found in WSL.");
                    return (false, null, "version.json not found");
                }
            }
            else
            {
                var path = GetVersionFilePath();
                if (!File.Exists(path)) return (false, null, "version.json not found");
                Console.WriteLine($"[CodexVersion] Reading version file {path}.");
                text = File.ReadAllText(path);
            }
            if (string.IsNullOrWhiteSpace(text)) return (false, null, "version.json empty");
            var obj = JObject.Parse(text);
            var version = obj["latest_version"]?.ToString();
            if (string.IsNullOrWhiteSpace(version)) return (false, null, "latest_version missing");
            return (true, version.Trim(), null);
        }
        catch (Exception ex)
        {
            return (false, null, ex.Message);
        }
    }

    public static async Task<(bool Ok, string? Version, string? Error)> TryGetInstalledVersionAsync(string command, string workingDirectory)
    {
        try
        {
            if (WslInterop.IsEnabled)
            {
                var baseCommand = string.IsNullOrWhiteSpace(command) ? "codex" : command;
                var shellCommand = $"{EscapeForShell(baseCommand)} --version";
                var psiWsl = WslInterop.CreateShellCommand(shellCommand);
                Console.WriteLine($"[CodexVersion] Running '{shellCommand}' via WSL.");
                return await TryGetInstalledVersionAsync(psiWsl);
            }

            if (string.IsNullOrWhiteSpace(command)) return (false, null, "Codex command not configured");

            var psi = new ProcessStartInfo
            {
                FileName = command,
                Arguments = "--version",
                WorkingDirectory = Directory.Exists(workingDirectory) ? workingDirectory : Directory.GetCurrentDirectory(),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            Console.WriteLine($"[CodexVersion] Running '{command} --version' in {psi.WorkingDirectory}.");

            return await TryGetInstalledVersionAsync(psi);
        }
        catch (Exception ex)
        {
            return (false, null, ex.Message);
        }
    }

    public static async Task<(bool Ok, string? Version, string? Error)> TryGetInstalledVersionAsync(ProcessStartInfo psi)
    {
        try
        {
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.UseShellExecute = false;

            using var process = Process.Start(psi);
            if (process is null) return (false, null, "Failed to start Codex CLI");

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            var combined = string.Join("\n", new[] { await stdoutTask, await stderrTask }
                .Where(s => !string.IsNullOrWhiteSpace(s))).Trim();
            if (string.IsNullOrWhiteSpace(combined))
                return (false, null, "Codex CLI returned no version info");

            var version = ExtractVersion(combined);
            if (string.IsNullOrWhiteSpace(version))
                return (false, null, "Unable to parse Codex version");

            return (true, version, null);
        }
        catch (Exception ex)
        {
            return (false, null, ex.Message);
        }
    }

    public static bool IsNewer(string latest, string current)
        => CompareVersions(latest, current) > 0;

    private static string? ExtractVersion(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var match = VersionRegex.Match(text);
        if (!match.Success) return null;
        var raw = match.Groups[1].Value.Trim();
        if (raw.StartsWith("v", StringComparison.OrdinalIgnoreCase)) raw = raw[1..];
        return raw;
    }

    private static int CompareVersions(string a, string b)
    {
        var ap = ParseParts(a);
        var bp = ParseParts(b);
        var max = Math.Max(ap.Length, bp.Length);
        for (int i = 0; i < max; i++)
        {
            var av = i < ap.Length ? ap[i] : 0;
            var bv = i < bp.Length ? bp[i] : 0;
            if (av != bv) return av.CompareTo(bv);
        }
        return 0;
    }

    private static int[] ParseParts(string version)
    {
        if (string.IsNullOrWhiteSpace(version)) return Array.Empty<int>();
        var clean = new string(version.TakeWhile(ch => char.IsDigit(ch) || ch == '.' || ch == '-').ToArray());
        if (string.IsNullOrWhiteSpace(clean)) clean = version;
        return clean
            .Split(new[] { '.', '-' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(part =>
            {
                var digits = new string(part.TakeWhile(char.IsDigit).ToArray());
                return int.TryParse(digits, out var value) ? value : 0;
            })
            .ToArray();
    }

    private static string EscapeForShell(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;
        if (value.IndexOf(' ') < 0 && value.IndexOf('\'') < 0 && value.IndexOf('"') < 0)
            return value;
        return "'" + value.Replace("'", "'\"'\"'") + "'";
    }

}
