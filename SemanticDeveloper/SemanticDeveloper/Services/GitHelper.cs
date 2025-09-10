using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LibGit2Sharp;
using System.Collections.Generic;

namespace SemanticDeveloper.Services;

public static class GitHelper
{
    public static bool IsGitRepository(string path)
    {
        try
        {
            var discovered = Repository.Discover(path);
            if (string.IsNullOrEmpty(discovered)) return false;

            var repoRoot = NormalizePath(Path.GetDirectoryName(discovered.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))!);
            var candidate = NormalizePath(path);
            return PathsEqual(repoRoot, candidate);
        }
        catch
        {
            return false;
        }
    }

    public static Task<bool> IsGitAvailableAsync()
    {
        try
        {
            // Force-load libgit2 by invoking a harmless API
            _ = Repository.Discover(Directory.GetCurrentDirectory());
            return Task.FromResult(true);
        }
        catch
        {
            // Native library not available or failed to load
            return Task.FromResult(false is bool ? (bool)false : false);
        }
    }

    public static Task<GitInitResult> InitializeRepositoryAsync(string path)
    {
        try
        {
            // Initialize repo (.git folder)
            var repoPath = Repository.Init(path);

            // Open and attempt an initial commit (best-effort)
            using var repo = new Repository(repoPath);

            // Stage everything
            try { Commands.Stage(repo, "*"); } catch { }

            // Create a signature using config or sensible fallbacks
            var author = CreateSignature(repo);
            try
            {
                // If nothing is staged, this may throw; swallow to keep behavior lenient
                repo.Commit("Initial commit", author, author);
            }
            catch { }

            return Task.FromResult(GitInitResult.CreateSuccess($"Initialized empty Git repository in {repo.Info.Path}"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(GitInitResult.CreateFailure(ex.Message));
        }
    }

    public static string? DiscoverRepositoryRoot(string path)
    {
        try
        {
            var discovered = Repository.Discover(path);
            if (string.IsNullOrEmpty(discovered)) return null;
            return NormalizePath(Path.GetDirectoryName(discovered.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))!);
        }
        catch
        {
            return null;
        }
    }

    public static (bool Ok, string? Branch, string? Error) TryGetCurrentBranch(string path)
    {
        try
        {
            var repoRoot = DiscoverRepositoryRoot(path);
            if (string.IsNullOrEmpty(repoRoot)) return (false, null, "Not a git repository");
            using var repo = new Repository(repoRoot!);
            var head = repo.Head;
            var name = head?.FriendlyName;
            if (string.IsNullOrWhiteSpace(name)) return (false, null, "No HEAD");
            return (true, name, null);
        }
        catch (Exception ex)
        {
            return (false, null, ex.Message);
        }
    }

    public static (bool Ok, string? Output, string? Error) TryCommitAll(string path, string message)
    {
        try
        {
            var repoRoot = DiscoverRepositoryRoot(path);
            if (string.IsNullOrEmpty(repoRoot)) return (false, null, "Not a git repository");
            using var repo = new Repository(repoRoot!);
            // Stage everything
            Commands.Stage(repo, "*");
            var author = CreateSignature(repo);
            // If nothing to commit, LibGit2Sharp throws; catch and return friendly
            try
            {
                var commit = repo.Commit(string.IsNullOrWhiteSpace(message) ? "Update" : message, author, author);
                return (true, $"Committed {commit.Sha[..7]}: {commit.MessageShort}", null);
            }
            catch (EmptyCommitException)
            {
                return (false, null, "No changes to commit.");
            }
        }
        catch (Exception ex)
        {
            return (false, null, ex.Message);
        }
    }

    public static (bool Ok, string? Output, string? Error) TryCreateAndCheckoutBranch(string path, string branchName)
        => TryCreateAndCheckoutBranch(path, branchName, baseOnDefaultBranch: false, fetchOriginFirst: false);

    public static (bool Ok, string? Output, string? Error) TryCreateAndCheckoutBranch(string path, string branchName, bool baseOnDefaultBranch, bool fetchOriginFirst)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(branchName)) return (false, null, "Invalid branch name");
            var repoRoot = DiscoverRepositoryRoot(path);
            if (string.IsNullOrEmpty(repoRoot)) return (false, null, "Not a git repository");
            using var repo = new Repository(repoRoot!);

            // If exists, just checkout
            var existing = repo.Branches.FirstOrDefault(b => string.Equals(b.FriendlyName, branchName, StringComparison.Ordinal));
            if (existing is not null)
            {
                Commands.Checkout(repo, existing);
                return (true, $"Checked out '{existing.FriendlyName}'", null);
            }

            Branch? baseBranch = null;
            Commit? baseCommit = null;

            if (fetchOriginFirst)
            {
                try
                {
                    var origin = repo.Network.Remotes["origin"];
                    if (origin is not null)
                    {
                        var refSpecs = origin.FetchRefSpecs.Select(x => x.Specification).ToList();
                        Commands.Fetch(repo, origin.Name, refSpecs, new FetchOptions(), null);
                    }
                }
                catch { }
            }

            if (baseOnDefaultBranch)
            {
                // Prefer remote default branches origin/main or origin/master, then local main/master, else current HEAD
                baseBranch = repo.Branches.FirstOrDefault(b => b.FriendlyName == "origin/main")
                             ?? repo.Branches.FirstOrDefault(b => b.FriendlyName == "origin/master")
                             ?? repo.Branches.FirstOrDefault(b => b.FriendlyName == "main")
                             ?? repo.Branches.FirstOrDefault(b => b.FriendlyName == "master");
                baseCommit = baseBranch?.Tip ?? repo.Head?.Tip;
            }
            else
            {
                baseCommit = repo.Head?.Tip;
            }

            if (baseCommit is null)
                return (false, null, "Repository has no commits to base from.");

            var created = repo.CreateBranch(branchName, baseCommit);
            Commands.Checkout(repo, created);
            var basedOn = baseBranch?.FriendlyName ?? "HEAD";
            return (true, $"Created and checked out '{created.FriendlyName}' (based on {basedOn}).", null);
        }
        catch (Exception ex)
        {
            return (false, null, ex.Message);
        }
    }

    public static (bool Ok, string? Output, string? Error) TryCheckoutBranch(string path, string branchName)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(branchName)) return (false, null, "Invalid branch name");
            var repoRoot = DiscoverRepositoryRoot(path);
            if (string.IsNullOrEmpty(repoRoot)) return (false, null, "Not a git repository");
            using var repo = new Repository(repoRoot!);
            var branch = repo.Branches.FirstOrDefault(b => string.Equals(b.FriendlyName, branchName, StringComparison.Ordinal));
            if (branch is null) return (false, null, $"Branch '{branchName}' not found.");
            Commands.Checkout(repo, branch);
            return (true, $"Checked out '{branch.FriendlyName}'", null);
        }
        catch (Exception ex)
        {
            return (false, null, ex.Message);
        }
    }

    public static (bool Ok, string? Content, string? Error) TryReadTextAtHead(string filePath)
    {
        try
        {
            var repoRoot = DiscoverRepositoryRoot(filePath);
            if (string.IsNullOrEmpty(repoRoot))
                return (false, null, "Not a git repository");

            var rel = Path.GetRelativePath(repoRoot!, filePath).Replace('\\', '/');
            using var repo = new Repository(repoRoot!);
            var head = repo.Head?.Tip;
            if (head == null)
                return (false, null, "No commits in repository");
            var entry = head[rel];
            if (entry == null || entry.TargetType != TreeEntryTargetType.Blob)
                return (false, null, "File not tracked in HEAD");
            var blob = (Blob)entry.Target;
            using var stream = blob.GetContentStream();
            using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, true);
            var text = reader.ReadToEnd();
            return (true, text, null);
        }
        catch (Exception ex)
        {
            return (false, null, ex.Message);
        }
    }

    public static (bool Ok, HashSet<int> BaseDeleted, HashSet<int> CurrentAdded, string? Error) TryGetLineDiffSets(string filePath)
    {
        var baseDeleted = new HashSet<int>();
        var currentAdded = new HashSet<int>();
        try
        {
            var repoRoot = DiscoverRepositoryRoot(filePath);
            if (string.IsNullOrEmpty(repoRoot))
                return (false, baseDeleted, currentAdded, "Not a git repository");

            using var repo = new Repository(repoRoot!);
            var rel = Path.GetRelativePath(repoRoot!, filePath).Replace('\\', '/');
            var headTree = repo.Head?.Tip?.Tree;
            if (headTree == null)
                return (false, baseDeleted, currentAdded, "No commits in repository");

            // Compare HEAD tree to working directory for the single file (unified diff text)
            var patch = repo.Diff.Compare<Patch>(headTree, DiffTargets.WorkingDirectory, new[] { rel });
            var entry = patch[rel];
            if (entry == null)
                return (true, baseDeleted, currentAdded, null); // No changes

            var diffText = entry.Patch ?? string.Empty;
            ParseUnifiedDiffForLineSets(diffText, baseDeleted, currentAdded);

            return (true, baseDeleted, currentAdded, null);
        }
        catch (Exception ex)
        {
            return (false, baseDeleted, currentAdded, ex.Message);
        }
    }

    private static void ParseUnifiedDiffForLineSets(string diff, HashSet<int> baseDeleted, HashSet<int> currentAdded)
    {
        // Parse lines with hunk headers @@ -oldStart,oldCount +newStart,newCount @@
        var lines = (diff ?? string.Empty).Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        int oldLine = 0, newLine = 0;
        foreach (var raw in lines)
        {
            var line = raw;
            if (line.StartsWith("+++ ") || line.StartsWith("--- ")) continue;
            if (line.StartsWith("@@"))
            {
                // Extract numbers
                // Example: @@ -12,7 +12,9 @@
                try
                {
                    int idxPlus = line.IndexOf('+');
                    int idxMinus = line.IndexOf('-');
                    int idxSecondAt = line.IndexOf("@@", 2);
                    var minusPart = line.Substring(idxMinus + 1, (idxPlus - idxMinus) - 2).Trim();
                    var plusPart = line.Substring(idxPlus + 1, (idxSecondAt - idxPlus) - 2).Trim();
                    oldLine = ParseStart(minusPart);
                    newLine = ParseStart(plusPart);
                }
                catch { oldLine = newLine = 0; }
                continue;
            }
            if (line.StartsWith("+"))
            {
                if (newLine > 0) currentAdded.Add(newLine);
                newLine++;
                continue;
            }
            if (line.StartsWith("-"))
            {
                if (oldLine > 0) baseDeleted.Add(oldLine);
                oldLine++;
                continue;
            }
            if (line.StartsWith("\\ No newline"))
            {
                continue;
            }
            // context
            if (oldLine > 0) oldLine++;
            if (newLine > 0) newLine++;
        }
    }

    private static int ParseStart(string range)
    {
        // range like "12,7" or "12"
        if (string.IsNullOrEmpty(range)) return 0;
        var idx = range.IndexOf(',');
        var first = idx >= 0 ? range.Substring(0, idx) : range;
        if (int.TryParse(first, out var val)) return val;
        return 0;
    }

    // Returns a map of full normalized file paths under the provided path to a simple status string
    // Values: "added", "modified", "deleted". Untracked files are treated as "added".
    public static System.Collections.Generic.Dictionary<string, string> TryGetWorkingDirectoryStatus(string path)
    {
        var result = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var repoRoot = DiscoverRepositoryRoot(path);
            if (string.IsNullOrEmpty(repoRoot)) return result;

            using var repo = new Repository(repoRoot!);
            var status = repo.RetrieveStatus(new StatusOptions
            {
                IncludeUntracked = true,
                RecurseIgnoredDirs = false,
                RecurseUntrackedDirs = true
            });

            var rootNorm = NormalizePath(path);
            foreach (var entry in status)
            {
                // Build full path and filter to selected subtree
                var full = NormalizePath(Path.Combine(repoRoot!, entry.FilePath.Replace('/', Path.DirectorySeparatorChar)));
                if (!full.StartsWith(rootNorm, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    continue;

                var kind = MapStatusToKind(entry.State);
                if (kind is null) continue;
                result[full] = kind;
            }
        }
        catch
        {
            // ignore, return partial/empty map
        }
        return result;
    }

    private static string? MapStatusToKind(FileStatus state)
    {
        if (state.HasFlag(FileStatus.NewInWorkdir) || state.HasFlag(FileStatus.NewInIndex)) return "added";
        if (state.HasFlag(FileStatus.ModifiedInWorkdir) || state.HasFlag(FileStatus.ModifiedInIndex)) return "modified";
        if (state.HasFlag(FileStatus.DeletedFromWorkdir) || state.HasFlag(FileStatus.DeletedFromIndex)) return "deleted";
        if (state.HasFlag(FileStatus.RenamedInWorkdir) || state.HasFlag(FileStatus.RenamedInIndex)) return "modified";
        if (state.HasFlag(FileStatus.TypeChangeInWorkdir) || state.HasFlag(FileStatus.TypeChangeInIndex)) return "modified";
        return null;
    }

    public static (bool Ok, string Status) TryGetStatusForFile(string filePath)
    {
        try
        {
            var repoRoot = DiscoverRepositoryRoot(filePath);
            if (string.IsNullOrEmpty(repoRoot)) return (false, string.Empty);
            using var repo = new Repository(repoRoot!);
            var rel = Path.GetRelativePath(repoRoot!, filePath).Replace('\\', '/');
            var st = repo.RetrieveStatus(rel);
            var kind = MapStatusToKind(st) ?? string.Empty;
            return (true, kind);
        }
        catch
        {
            return (false, string.Empty);
        }
    }

    private static Signature CreateSignature(Repository repo)
    {
        string? name = null;
        string? email = null;
        try { name = repo.Config.Get<string>("user.name")?.Value; } catch { }
        try { email = repo.Config.Get<string>("user.email")?.Value; } catch { }

        if (string.IsNullOrWhiteSpace(name)) name = Environment.UserName ?? "User";
        if (string.IsNullOrWhiteSpace(email)) email = $"{name}@local";
        return new Signature(name, email, DateTimeOffset.Now);
    }

    private static string NormalizePath(string p)
        => Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool PathsEqual(string a, string b)
        => OperatingSystem.IsWindows()
            ? string.Equals(a, b, StringComparison.OrdinalIgnoreCase)
            : string.Equals(a, b, StringComparison.Ordinal);
}

public record GitInitResult(bool Success, string Output, string Error)
{
    public static GitInitResult CreateSuccess(string output) => new(true, output, string.Empty);
    public static GitInitResult CreateFailure(string error) => new(false, string.Empty, error);
}
