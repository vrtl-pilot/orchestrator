using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Orchestrator.Core.Model;

namespace Orchestrator.Core.Git;

public sealed record WorktreeInfo(string Path, string Branch, string BaseSha, bool IncludesUncommittedChanges);

public sealed record DiffInfo(string HeadSha, string DiffStat, IReadOnlyList<ChangedFile> Files, string Patch);

/// <summary>
/// Creates one worktree + branch per task, so each agent has exactly one private working directory.
/// All operations that touch shared refs or the worktree list are serialized per repository.
/// </summary>
public sealed class WorktreeManager(IOptions<OrchestratorOptions> options)
{
    private const string CommitterName = "Agent Orchestrator";
    private const string CommitterEmail = "orchestrator@localhost";

    private readonly OrchestratorOptions _options = options.Value;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _repoLocks = new(StringComparer.Ordinal);

    /// <summary>Resolves the repository root for any path inside a Git working tree.</summary>
    public static async Task<string> GetRepoRootAsync(string path, CancellationToken ct = default)
    {
        if (!Directory.Exists(path))
        {
            throw new GitException($"Directory '{path}' does not exist.");
        }
        var root = await GitClient.RunCheckedAsync(path, ["rev-parse", "--show-toplevel"], cancellationToken: ct);
        return Path.GetFullPath(root);
    }

    public async Task<WorktreeInfo> CreateAsync(
        string repoRoot, string taskId, string? baseRef, bool includeUncommitted, CancellationToken ct = default)
    {
        using var _ = await LockAsync(repoRoot, ct);

        var baseSha = await GitClient.RunCheckedAsync(
            repoRoot, ["rev-parse", "--verify", $"{baseRef ?? "HEAD"}^{{commit}}"], cancellationToken: ct);

        await EnsureExcludedAsync(repoRoot, ct);

        var snapshotted = false;
        if (baseRef is null && includeUncommitted)
        {
            var snapshot = await SnapshotWorkingTreeAsync(repoRoot, baseSha, taskId, ct);
            if (snapshot is not null)
            {
                baseSha = snapshot;
                snapshotted = true;
            }
        }

        var branch = _options.BranchPrefix + taskId;
        var path = Path.GetFullPath(Path.Combine(repoRoot, _options.WorktreeDirectory, taskId));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        await GitClient.RunCheckedAsync(repoRoot, ["worktree", "add", "-b", branch, path, baseSha], cancellationToken: ct);
        await GitClient.RunAsync(repoRoot, ["worktree", "lock", "--reason", $"orchestrator task {taskId}", path], cancellationToken: ct);

        CopyIncludedFiles(repoRoot, path);
        return new WorktreeInfo(path, branch, baseSha, snapshotted);
    }

    /// <summary>Commits everything the agent left uncommitted. Returns false when there was nothing to commit.</summary>
    public async Task<bool> CommitAllAsync(string worktreePath, string message, CancellationToken ct = default)
    {
        await GitClient.RunCheckedAsync(worktreePath, ["add", "-A"], cancellationToken: ct);
        var staged = await GitClient.RunAsync(worktreePath, ["diff", "--cached", "--quiet"], cancellationToken: ct);
        if (staged.ExitCode == 0) return false;

        await GitClient.RunCheckedAsync(
            worktreePath,
            [.. await IdentityArgsAsync(worktreePath, ct), "commit", "--no-verify", "-q", "-m", message],
            cancellationToken: ct);
        return true;
    }

    public async Task<DiffInfo> DiffAsync(string worktreePath, string baseSha, CancellationToken ct = default)
    {
        var head = await GitClient.RunCheckedAsync(worktreePath, ["rev-parse", "HEAD"], cancellationToken: ct);
        var stat = await GitClient.RunCheckedAsync(worktreePath, ["diff", "--stat", baseSha, head], cancellationToken: ct);
        var nameStatus = await GitClient.RunCheckedAsync(worktreePath, ["diff", "--name-status", baseSha, head], cancellationToken: ct);
        var patch = await GitClient.RunCheckedAsync(worktreePath, ["diff", "--binary", baseSha, head], cancellationToken: ct);

        var files = nameStatus
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split('\t'))
            .Where(parts => parts.Length >= 2)
            .Select(parts => new ChangedFile(parts[0], parts[^1]))
            .ToList();

        return new DiffInfo(head, stat, files, patch);
    }

    public async Task RemoveAsync(string repoRoot, string worktreePath, string? branch, bool deleteBranch, CancellationToken ct = default)
    {
        using var _ = await LockAsync(repoRoot, ct);
        await GitClient.RunAsync(repoRoot, ["worktree", "unlock", worktreePath], cancellationToken: ct);
        var removed = await GitClient.RunAsync(repoRoot, ["worktree", "remove", "--force", "--force", worktreePath], cancellationToken: ct);
        if (!removed.Ok && Directory.Exists(worktreePath))
        {
            Directory.Delete(worktreePath, recursive: true);
        }
        await GitClient.RunAsync(repoRoot, ["worktree", "prune"], cancellationToken: ct);
        if (deleteBranch && branch is not null)
        {
            await GitClient.RunAsync(repoRoot, ["branch", "-D", branch], cancellationToken: ct);
        }
    }

    /// <summary>
    /// Captures tracked + untracked (non-ignored) changes as a commit on top of HEAD without touching the
    /// caller's index or working tree, using a temporary index file. Returns null when the tree is clean.
    /// </summary>
    private async Task<string?> SnapshotWorkingTreeAsync(string repoRoot, string headSha, string taskId, CancellationToken ct)
    {
        var tempIndex = Path.Combine(Path.GetTempPath(), $"orchestrator-index-{taskId}");
        var env = new Dictionary<string, string?> { ["GIT_INDEX_FILE"] = tempIndex };
        try
        {
            await GitClient.RunCheckedAsync(repoRoot, ["read-tree", headSha], env, ct);
            // The worktree folder is already ignored via info/exclude (EnsureExcludedAsync runs first).
            await GitClient.RunCheckedAsync(repoRoot, ["add", "-A"], env, ct);
            var tree = await GitClient.RunCheckedAsync(repoRoot, ["write-tree"], env, ct);
            var headTree = await GitClient.RunCheckedAsync(repoRoot, ["rev-parse", $"{headSha}^{{tree}}"], cancellationToken: ct);
            if (tree == headTree) return null;

            var commit = await GitClient.RunCheckedAsync(
                repoRoot,
                [.. await IdentityArgsAsync(repoRoot, ct), "commit-tree", tree, "-p", headSha, "-m",
                    $"orchestrator: snapshot of uncommitted changes for task {taskId}"],
                cancellationToken: ct);
            // Keep the snapshot reachable for as long as the task exists.
            await GitClient.RunCheckedAsync(repoRoot, ["update-ref", $"refs/orchestrator/base/{taskId}", commit], cancellationToken: ct);
            return commit;
        }
        finally
        {
            File.Delete(tempIndex);
        }
    }

    /// <summary>Hides the worktree folder from <c>git status</c> without modifying tracked files.</summary>
    private async Task EnsureExcludedAsync(string repoRoot, CancellationToken ct)
    {
        var commonDir = await GitClient.RunCheckedAsync(repoRoot, ["rev-parse", "--git-common-dir"], cancellationToken: ct);
        var excludeFile = Path.Combine(Path.GetFullPath(commonDir, repoRoot), "info", "exclude");
        var pattern = $"/{WorktreeTopDirectory}/";

        Directory.CreateDirectory(Path.GetDirectoryName(excludeFile)!);
        var existing = File.Exists(excludeFile) ? await File.ReadAllLinesAsync(excludeFile, ct) : [];
        if (!existing.Contains(pattern))
        {
            await File.AppendAllTextAsync(excludeFile, $"{Environment.NewLine}{pattern}{Environment.NewLine}", ct);
        }
    }

    /// <summary>First segment of <see cref="OrchestratorOptions.WorktreeDirectory"/>, e.g. <c>.orchestrator</c>.</summary>
    private string WorktreeTopDirectory =>
        _options.WorktreeDirectory.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)[0];

    private void CopyIncludedFiles(string repoRoot, string worktreePath)
    {
        foreach (var relative in _options.CopyIntoWorktree)
        {
            var source = Path.Combine(repoRoot, relative);
            var target = Path.Combine(worktreePath, relative);
            if (File.Exists(source))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target, overwrite: true);
            }
            else if (Directory.Exists(source))
            {
                CopyDirectory(source, target);
            }
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        }
        foreach (var dir in Directory.GetDirectories(source))
        {
            CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)));
        }
    }

    /// <summary>Supplies a fallback committer identity only when the repository has none configured.</summary>
    private static async Task<string[]> IdentityArgsAsync(string repo, CancellationToken ct)
    {
        var email = await GitClient.RunAsync(repo, ["config", "user.email"], cancellationToken: ct);
        return email.Ok && email.StdOut.Length > 0
            ? []
            : ["-c", $"user.name={CommitterName}", "-c", $"user.email={CommitterEmail}"];
    }

    private async Task<IDisposable> LockAsync(string repoRoot, CancellationToken ct)
    {
        var gate = _repoLocks.GetOrAdd(repoRoot, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        return new Releaser(gate);
    }

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }
}
