using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System.IO;

namespace SemanticDeveloper.Services;

public class CodexCliService
{
    private Process? _process;

    public string Command { get; set; } = "codex"; // Assumes codex CLI is on PATH
    public bool UseProto => true;                   // Always use --proto for this app
    public string AdditionalArgs { get; set; } = string.Empty; // Extra CLI args if needed
    public bool UseWsl { get; set; } = false;        // Windows: run via wsl.exe when true
    public bool IsRunning => _process is { HasExited: false };
    public bool UseApiKey { get; set; } = false;
    public string? ApiKey { get; set; }

    public event EventHandler<string>? OutputReceived;
    public event EventHandler? Exited;

    private enum ProtoMode { Flag, Subcommand, None }
    private ProtoMode? _detectedMode;

    public async Task StartAsync(string workspacePath, CancellationToken cancellationToken)
    {
        if (IsRunning)
            throw new InvalidOperationException("CLI already running.");

        // Force proto subcommand per environment ("codex proto")
        var mode = ProtoMode.Subcommand;

        var tokens = BuildArgumentTokens(mode, AdditionalArgs);
        var effectiveWorkspace = string.IsNullOrWhiteSpace(workspacePath) ? Directory.GetCurrentDirectory() : workspacePath;
        var psi = await BuildProcessStartInfoAsync(effectiveWorkspace, tokens, redirectStdIn: true);

        // Do not inject API key via environment. Authentication is handled via an explicit
        // 'codex login --api-key' flow before starting the proto session.

        // Emit the exact command being launched to the debug console (not UI)
        try
        {
            var rendered = RenderCommandForDisplay(psi.FileName, psi.ArgumentList.Cast<string>(), psi.WorkingDirectory);
            Debug.WriteLine($"[CodexCLI] {rendered}");
            Trace.WriteLine($"[CodexCLI] {rendered}");
        }
        catch { }

        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => { if (e.Data != null) OutputReceived?.Invoke(this, e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) OutputReceived?.Invoke(this, e.Data); };
        p.Exited += (_, __) => Exited?.Invoke(this, EventArgs.Empty);

        try
        {
            if (!p.Start())
                throw new InvalidOperationException("Failed to start CLI process.");
        }
        catch (Exception ex)
        {
            OutputReceived?.Invoke(this, $"Failed to start '{Command}': {ex.Message}");
            p.Dispose();
            throw;
        }

        _process = p;
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        // Optional: monitor cancellation to stop the process
        if (cancellationToken.CanBeCanceled)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(-1, cancellationToken);
                    Stop();
                }
                catch (TaskCanceledException) { }
            });
        }
    }

    public void Stop()
    {
        try
        {
            if (_process is { HasExited: false } p)
            {
                try
                {
                    p.Kill(true);
                }
                catch
                {
                    // ignore
                }
                finally
                {
                    p.Dispose();
                }
            }
        }
        finally
        {
            _process = null;
        }
    }

    public Task SendAsync(string data)
    {
        if (_process is not { HasExited: false } p)
            throw new InvalidOperationException("CLI is not running.");
        return p.StandardInput.WriteLineAsync(data);
    }

    private async Task<ProtoMode> DetectProtoModeAsync(string workingDir)
    {
        if (_detectedMode is { } cached)
            return cached;

        try
        {
            var (codeFlag, _, _) = await RunArgsAsync(new[] { "--proto", "--help" }, workingDir);
            if (codeFlag == 0)
            {
                _detectedMode = ProtoMode.Flag;
                return _detectedMode.Value;
            }

            var (codeSub, _, _) = await RunArgsAsync(new[] { "proto", "--help" }, workingDir);
            if (codeSub == 0)
            {
                _detectedMode = ProtoMode.Subcommand;
                return _detectedMode.Value;
            }

            // Fallback: attempt to detect from general help text
            var help = await RunHelpTextAsync(workingDir);
            if (help.IndexOf("--proto", StringComparison.OrdinalIgnoreCase) >= 0)
                _detectedMode = ProtoMode.Flag;
            else if (help.IndexOf(" proto\n", StringComparison.Ordinal) >= 0 || help.IndexOf("\n  proto ", StringComparison.Ordinal) >= 0 || help.Contains("SUBCOMMANDS") && help.IndexOf("proto", StringComparison.OrdinalIgnoreCase) >= 0)
                _detectedMode = ProtoMode.Subcommand;
            else
                _detectedMode = ProtoMode.None;
        }
        catch
        {
            _detectedMode = ProtoMode.None;
        }

        return _detectedMode.Value;
    }

    private async Task<string> RunHelpTextAsync(string workingDir)
    {
        try
        {
            var psi = await BuildProcessStartInfoAsync(workingDir, new[] { "--help" }, redirectStdIn: false);
            using var p = Process.Start(psi);
            if (p is null) return string.Empty;
            var stdout = await p.StandardOutput.ReadToEndAsync();
            var stderr = await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            return string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private async Task<(int Exit, string Stdout, string Stderr)> RunArgsAsync(IEnumerable<string> args, string workingDir)
    {
        var psi = await BuildProcessStartInfoAsync(workingDir, args, redirectStdIn: false);
        using var p = new Process { StartInfo = psi };
        p.Start();
        var stdout = await p.StandardOutput.ReadToEndAsync();
        var stderr = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (p.ExitCode, stdout, stderr);
    }

    internal async Task<ProcessStartInfo> BuildProcessStartInfoAsync(string workingDir, IEnumerable<string> commandArgs, bool redirectStdIn, bool redirectStdOut = true, bool redirectStdErr = true)
    {
        var effectiveDir = string.IsNullOrWhiteSpace(workingDir) ? Directory.GetCurrentDirectory() : workingDir;
        var psi = new ProcessStartInfo
        {
            WorkingDirectory = effectiveDir,
            RedirectStandardInput = redirectStdIn,
            RedirectStandardOutput = redirectStdOut,
            RedirectStandardError = redirectStdErr,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (UseWsl && OperatingSystem.IsWindows())
        {
            var wslDir = await TranslatePathToWslAsync(effectiveDir);
            psi.FileName = "wsl";
            if (!string.IsNullOrWhiteSpace(wslDir))
            {
                psi.ArgumentList.Add("--cd");
                psi.ArgumentList.Add(wslDir);
            }
            psi.ArgumentList.Add("--exec");
            psi.ArgumentList.Add(Command);
        }
        else
        {
            psi.FileName = Command;
        }

        foreach (var arg in commandArgs ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(arg)) continue;
            var normalized = NormalizeToken(arg);
            if (string.IsNullOrWhiteSpace(normalized)) continue;
            psi.ArgumentList.Add(normalized);
        }

        return psi;
    }

    private async Task<string> TranslatePathToWslAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "/";
        if (!UseWsl || !OperatingSystem.IsWindows()) return path;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "wsl",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("wslpath");
            psi.ArgumentList.Add("-a");
            psi.ArgumentList.Add(path);

            using var proc = Process.Start(psi);
            if (proc != null)
            {
                var stdout = await proc.StandardOutput.ReadToEndAsync();
                await proc.WaitForExitAsync();
                if (proc.ExitCode == 0)
                {
                    var converted = stdout.Trim();
                    if (!string.IsNullOrWhiteSpace(converted))
                        return converted.Replace('\\', '/');
                }
            }
        }
        catch { }

        return FallbackWindowsPathToWsl(path);
    }

    private static string FallbackWindowsPathToWsl(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "/";
        var trimmed = path.Trim();
        var normalized = trimmed.Replace('\\', '/');
        if (normalized.Length >= 2 && normalized[1] == ':' && char.IsLetter(normalized[0]))
        {
            var drive = char.ToLowerInvariant(normalized[0]);
            var rest = normalized.Substring(2).TrimStart('/');
            return rest.Length == 0 ? $"/mnt/{drive}" : $"/mnt/{drive}/{rest}";
        }
        return normalized.StartsWith('/') ? normalized : "/" + normalized.TrimStart('/');
    }

    private static string NormalizeToken(string token)
    {
        if (string.IsNullOrEmpty(token)) return token;
        if (token.Length >= 2)
        {
            var first = token[0];
            var last = token[^1];
            if ((first == '"' && last == '"') || (first == '\'' && last == '\''))
                return token.Substring(1, token.Length - 2);
        }
        return token;
    }

    private static List<string> BuildArgumentTokens(ProtoMode mode, string? additional)
    {
        var tokens = new List<string>();
        switch (mode)
        {
            case ProtoMode.Flag: tokens.Add("--proto"); break;
            case ProtoMode.Subcommand: tokens.Add("proto"); break;
        }
        if (!string.IsNullOrWhiteSpace(additional))
        {
            tokens.AddRange(SplitArgsRespectingQuotes(additional!));
        }
        return tokens;
    }

    private static string RenderCommandForDisplay(string fileName, IEnumerable<string> args, string workingDir)
    {
        static string Quote(string s)
        {
            if (string.IsNullOrEmpty(s)) return "\"\"";
            bool needs = s.Any(char.IsWhiteSpace);
            if (!needs) return s;
            var escaped = s.Replace("\\", "\\\\").Replace("\"", "\\\"");
            return "\"" + escaped + "\"";
        }

        var joined = string.Join(" ", new[] { fileName }.Concat(args.Select(Quote)));
        var prefix = string.IsNullOrWhiteSpace(workingDir) ? string.Empty : (workingDir + "$ ");
        return prefix + joined;
    }

    private static IEnumerable<string> SplitArgsRespectingQuotes(string input)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(input)) return result;
        var sb = new System.Text.StringBuilder();
        bool inSingle = false, inDouble = false;
        int bracketDepth = 0;
        for (int i = 0; i < input.Length; i++)
        {
            char c = input[i];
            if (c == '\'' && !inDouble) { inSingle = !inSingle; sb.Append(c); continue; }
            if (c == '"' && !inSingle) { inDouble = !inDouble; sb.Append(c); continue; }
            if (!inSingle && !inDouble)
            {
                if (c == '[') { bracketDepth++; sb.Append(c); continue; }
                if (c == ']') { bracketDepth = Math.Max(0, bracketDepth - 1); sb.Append(c); continue; }
                if (char.IsWhiteSpace(c) && bracketDepth == 0)
                {
                    if (sb.Length > 0) { result.Add(sb.ToString()); sb.Clear(); }
                    continue;
                }
            }
            sb.Append(c);
        }
        if (sb.Length > 0) result.Add(sb.ToString());
        return result;
    }
}
