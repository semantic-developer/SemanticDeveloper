using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace SemanticDeveloper.Services;

public static class GitHelper
{
    public static bool IsGitRepository(string path)
        => Directory.Exists(Path.Combine(path, ".git"));

    public static async Task<bool> IsGitAvailableAsync()
    {
        try
        {
            var (code, _, _) = await RunProcessAsync("git", "--version", Directory.GetCurrentDirectory());
            return code == 0;
        }
        catch
        {
            return false;
        }
    }

    public static async Task<GitInitResult> InitializeRepositoryAsync(string path)
    {
        try
        {
            var (codeInit, outInit, errInit) = await RunProcessAsync("git", "init", path);
            if (codeInit != 0)
                return GitInitResult.CreateFailure(errInit);

            // Attempt initial commit (optional)
            await RunProcessAsync("git", "add -A", path);
            await RunProcessAsync("git", "commit -m \"Initial commit\"", path);

            return GitInitResult.CreateSuccess(outInit);
        }
        catch (Exception ex)
        {
            return GitInitResult.CreateFailure(ex.Message);
        }
    }

    private static Task<(int ExitCode, string StdOut, string StdErr)> RunProcessAsync(string fileName, string arguments, string workingDirectory)
    {
        var tcs = new TaskCompletionSource<(int, string, string)>();
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stdout = string.Empty;
        var stderr = string.Empty;
        process.OutputDataReceived += (_, e) => { if (e.Data != null) stdout += e.Data + Environment.NewLine; };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr += e.Data + Environment.NewLine; };
        process.Exited += (_, __) =>
        {
            tcs.TrySetResult((process.ExitCode, stdout, stderr));
            process.Dispose();
        };
        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            tcs.TrySetException(ex);
        }
        return tcs.Task;
    }
}

public record GitInitResult(bool Success, string Output, string Error)
{
    public static GitInitResult CreateSuccess(string output) => new(true, output, string.Empty);
    public static GitInitResult CreateFailure(string error) => new(false, string.Empty, error);
}
