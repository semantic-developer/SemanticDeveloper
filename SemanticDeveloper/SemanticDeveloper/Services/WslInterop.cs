using System;
using System.Diagnostics;
using System.IO;

namespace SemanticDeveloper.Services;

/// <summary>
/// Best-effort helpers for interacting with the default WSL distribution.
/// </summary>
internal static class WslInterop
{
    private static readonly object Sync = new();
    private static bool _requested;
    private static bool _enabled;
    private static string? _wslExePath;
    private static string? _distribution;

    public static string? SelectedDistribution
    {
        get
        {
            lock (Sync)
            {
                return _distribution;
            }
        }
    }

    public static string? GetWslExecutable()
    {
        lock (Sync)
        {
            return _wslExePath;
        }
    }

    public static void SetUseWsl(bool useWsl)
    {
        lock (Sync)
        {
            _requested = useWsl && OperatingSystem.IsWindows();
            _enabled = _requested && TryResolveWslExe(out _wslExePath);
            _distribution = null;

            if (!_enabled)
            {
                if (_requested)
                    Console.WriteLine("[WSL] Requested but wsl.exe not found; falling back to Windows paths.");
                _wslExePath = null;
                return;
            }

            Console.WriteLine($"[WSL] Using wsl.exe at '{_wslExePath}'.");

            if (!TrySelectDistribution(out _distribution))
            {
                Console.WriteLine("[WSL] Failed to determine a suitable distribution; WSL access disabled.");
                _enabled = false;
                _wslExePath = null;
                _distribution = null;
            }
            else
            {
                Console.WriteLine($"[WSL] Selected distribution '{_distribution}'.");
            }
        }
    }

    public static bool IsEnabled
    {
        get
        {
            lock (Sync)
            {
                return _enabled;
            }
        }
    }

    public static string? TryConvertToWindowsPath(string wslPath)
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(wslPath))
            return null;

        var psi = CreateBaseProcessStartInfo(includeDistribution: true);
        var (exitCode, stdout, _) = RunProcess(psi, "wslpath", "-w", wslPath);

        if (exitCode == 0)
        {
            var text = stdout.Trim();
            if (!string.IsNullOrWhiteSpace(text))
            {
                Console.WriteLine($"[WSL] wslpath -w '{wslPath}' -> '{text}'.");
                return text;
            }
        }
        Console.WriteLine($"[WSL] Failed to convert '{wslPath}' (exit {exitCode}).");
        return null;
    }

    public static string? ReadFile(string wslPath)
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(wslPath))
            return null;

        var command = $"cat {EscapeForShell(wslPath)}";
        var psi = CreateShellCommand(command);
        var (exitCode, stdout, stderr) = RunProcess(psi);
        if (exitCode == 0)
            return stdout;
        Console.WriteLine($"[WSL] Read failed for '{wslPath}' (exit {exitCode}): {stderr}");
        return null;
    }

    public static ProcessStartInfo CreateShellCommand(string command)
    {
        if (!IsEnabled)
            throw new InvalidOperationException("WSL is not available.");

        var psi = CreateBaseProcessStartInfo(includeDistribution: true);

        psi.ArgumentList.Add("sh");
        psi.ArgumentList.Add("-lc");
        psi.ArgumentList.Add(command);
        return psi;
    }

    private static string EscapeForShell(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "''";

        if (value == "~")
            value = "$HOME";
        else if (value.StartsWith("~/", StringComparison.Ordinal))
            value = "$HOME/" + value.Substring(2);

        if (!value.Contains('\''))
            return $"'{value}'";

        return "'" + value.Replace("'", "'\"'\"'") + "'";
    }

    private static (int ExitCode, string StdOut, string StdErr) RunProcess(ProcessStartInfo psi, params string[] extraArgs)
    {
        foreach (var arg in extraArgs)
            psi.ArgumentList.Add(arg);

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
                return (-1, string.Empty, "Failed to start process");

            var stdout = psi.RedirectStandardOutput ? process.StandardOutput.ReadToEnd() : string.Empty;
            var stderr = psi.RedirectStandardError ? process.StandardError.ReadToEnd() : string.Empty;

            if (!string.IsNullOrEmpty(stdout)) stdout = stdout.Replace("\0", string.Empty);
            if (!string.IsNullOrEmpty(stderr)) stderr = stderr.Replace("\0", string.Empty);
            process.WaitForExit();
            if (process.ExitCode != 0)
                Console.WriteLine($"[WSL] Command '{psi.FileName} {string.Join(' ', psi.ArgumentList)}' failed with exit {process.ExitCode}: {stderr}");
            return (process.ExitCode, stdout, stderr);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WSL] Command '{psi.FileName} {string.Join(' ', psi.ArgumentList)}' threw {ex.Message}.");
            return (-1, string.Empty, ex.Message);
        }
    }

    private static string? GetWslExePath()
    {
        lock (Sync)
        {
            return _wslExePath;
        }
    }

    private static bool TryResolveWslExe(out string? path)
    {
        path = null;
        try
        {
            var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
            if (!string.IsNullOrWhiteSpace(system32))
            {
                var candidate = Path.Combine(system32, "wsl.exe");
                if (File.Exists(candidate))
                {
                    path = candidate;
                    return true;
                }
            }
        }
        catch { }

        try
        {
            var windir = Environment.GetEnvironmentVariable("WINDIR");
            if (!string.IsNullOrWhiteSpace(windir))
            {
                var candidate = Path.Combine(windir, "System32", "wsl.exe");
                if (File.Exists(candidate))
                {
                    path = candidate;
                    return true;
                }
            }
        }
        catch { }

        Console.WriteLine("[WSL] Unable to locate wsl.exe; WSL features disabled.");
        return false;
    }

    private static bool TrySelectDistribution(out string? distribution)
    {
        distribution = null;

        var psi = CreateBaseProcessStartInfo(includeDistribution: false);
        var (exitCode, stdout, stderr) = RunProcess(psi, "-l");
        if (exitCode != 0)
        {
            Console.WriteLine($"[WSL] Failed to list distributions (-l): {stderr}");
            return false;
        }

        string? defaultDistro = null;
        var candidates = new System.Collections.Generic.List<string>();

        foreach (var raw in stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (string.IsNullOrWhiteSpace(line))
                continue;
            if (line.StartsWith("Windows Subsystem for Linux Distributions:", StringComparison.OrdinalIgnoreCase))
                continue;

            bool isDefault = line.StartsWith("*", StringComparison.Ordinal);
            if (isDefault)
            {
                line = line.TrimStart('*', ' ', '\t');
            }

            var clean = line.Replace("(Default)", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
            if (string.IsNullOrWhiteSpace(clean))
                continue;

            if (clean.Equals("docker-desktop", StringComparison.OrdinalIgnoreCase) ||
                clean.Equals("docker-desktop-data", StringComparison.OrdinalIgnoreCase) ||
                clean.Equals("windows subsystem for linux distributions:", StringComparison.OrdinalIgnoreCase))
                continue;

            candidates.Add(clean);
            if (isDefault)
                defaultDistro = clean;
        }

        if (candidates.Count == 0)
        {
            Console.WriteLine("[WSL] No usable distributions found; disabling WSL interop.");
            return false;
        }

        Console.WriteLine("[WSL] Available distributions: " + string.Join(", ", candidates));

        if (defaultDistro != null && !IsDockerDistribution(defaultDistro))
        {
            distribution = defaultDistro;
            return true;
        }

        if (defaultDistro != null)
            Console.WriteLine($"[WSL] Default distribution '{defaultDistro}' is not suitable.");

        foreach (var entry in candidates)
        {
            if (!IsDockerDistribution(entry))
            {
                distribution = entry;
                Console.WriteLine($"[WSL] Selected non-default distribution '{entry}'.");
                return true;
            }
        }

        Console.WriteLine("[WSL] No suitable non-docker distribution found.");
        return false;
    }

    private static bool IsDockerDistribution(string name)
        => name.Replace(" ", string.Empty, StringComparison.OrdinalIgnoreCase)
                .StartsWith("docker", StringComparison.OrdinalIgnoreCase);

    public static ProcessStartInfo CreateBaseProcessStartInfo(bool includeDistribution)
    {
        var psi = new ProcessStartInfo
        {
            FileName = GetWslExePath()!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (includeDistribution && !string.IsNullOrWhiteSpace(_distribution))
        {
            psi.ArgumentList.Add("-d");
            psi.ArgumentList.Add(_distribution!);
        }

        return psi;
    }
}
