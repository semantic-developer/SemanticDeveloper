using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SemanticDeveloper.Services;

public class CodexCliService
{
    private Process? _process;

    public string Command { get; set; } = "codex"; // Assumes codex CLI is on PATH
    public bool UseProto => true;                   // Always use --proto for this app
    public string AdditionalArgs { get; set; } = string.Empty; // Extra CLI args if needed
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

        var psi = new ProcessStartInfo
        {
            FileName = Command,
            WorkingDirectory = workspacePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // Build argument tokens to avoid shell-quoting pitfalls
        var tokens = BuildArgumentTokens(mode, AdditionalArgs);
        foreach (var t in tokens)
            psi.ArgumentList.Add(t);

        // Do not inject API key via environment. Authentication is handled via an explicit
        // 'codex login --api-key' flow before starting the proto session.

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
            var (codeFlag, _, _) = await RunArgsAsync("--proto --help", workingDir);
            if (codeFlag == 0)
            {
                _detectedMode = ProtoMode.Flag;
                return _detectedMode.Value;
            }

            var (codeSub, _, _) = await RunArgsAsync("proto --help", workingDir);
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
        var psi = new ProcessStartInfo
        {
            FileName = Command,
            Arguments = "--help",
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        try
        {
            using var p = Process.Start(psi)!;
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

    private async Task<(int Exit, string Stdout, string Stderr)> RunArgsAsync(string args, string workingDir)
    {
        var psi = new ProcessStartInfo
        {
            FileName = Command,
            Arguments = args,
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var p = new Process { StartInfo = psi };
        p.Start();
        var stdout = await p.StandardOutput.ReadToEndAsync();
        var stderr = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (p.ExitCode, stdout, stderr);
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
