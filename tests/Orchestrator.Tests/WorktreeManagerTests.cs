using Microsoft.Extensions.Options;
using Orchestrator.Core;
using Orchestrator.Core.Git;

namespace Orchestrator.Tests;

public class WorktreeManagerTests
{
    private static WorktreeManager Manager() => new(Options.Create(new OrchestratorOptions()));

    [Fact]
    public async Task Worktree_includes_callers_uncommitted_and_untracked_changes_without_touching_them()
    {
        using var repo = await TestRepo.CreateAsync();
        repo.Write("README.md", "hello, edited but not committed\n");
        repo.Write("notes/new.txt", "untracked file\n");
        var statusBefore = await repo.GitAsync("status", "--porcelain");

        var wt = await Manager().CreateAsync(repo.Root, "t1", baseRef: null, includeUncommitted: true);

        Assert.True(wt.IncludesUncommittedChanges);
        Assert.Equal("orchestrator/t1", wt.Branch);
        Assert.Equal("hello, edited but not committed\n", File.ReadAllText(Path.Combine(wt.Path, "README.md")));
        Assert.True(File.Exists(Path.Combine(wt.Path, "notes", "new.txt")));

        // The caller's working tree and index are untouched, and the worktree folder is hidden from status.
        Assert.Equal(statusBefore, await repo.GitAsync("status", "--porcelain"));
    }

    [Fact]
    public async Task Clean_repo_branches_from_head_and_diff_reports_agent_changes()
    {
        using var repo = await TestRepo.CreateAsync();
        var head = await repo.GitAsync("rev-parse", "HEAD");
        var manager = Manager();

        var wt = await manager.CreateAsync(repo.Root, "t2", null, includeUncommitted: true);
        Assert.False(wt.IncludesUncommittedChanges);
        Assert.Equal(head, wt.BaseSha);

        File.WriteAllText(Path.Combine(wt.Path, "feature.txt"), "new feature\n");
        File.Delete(Path.Combine(wt.Path, "README.md"));

        Assert.True(await manager.CommitAllAsync(wt.Path, "agent work"));
        Assert.False(await manager.CommitAllAsync(wt.Path, "nothing left"));

        var diff = await manager.DiffAsync(wt.Path, wt.BaseSha);
        Assert.Contains(diff.Files, f => f is { Status: "A", Path: "feature.txt" });
        Assert.Contains(diff.Files, f => f is { Status: "D", Path: "README.md" });
        Assert.Contains("feature.txt", diff.Patch);

        // The caller can merge the branch like any other.
        await repo.GitAsync("merge", "-q", "--no-ff", "-m", "merge agent work", wt.Branch);
        Assert.True(File.Exists(Path.Combine(repo.Root, "feature.txt")));

        await manager.RemoveAsync(repo.Root, wt.Path, wt.Branch, deleteBranch: true);
        Assert.False(Directory.Exists(wt.Path));
        Assert.DoesNotContain("orchestrator/t2", await repo.GitAsync("branch", "--list"));
    }

    [Fact]
    public async Task Base_ref_is_respected_and_uncommitted_changes_ignored()
    {
        using var repo = await TestRepo.CreateAsync();
        var first = await repo.GitAsync("rev-parse", "HEAD");
        repo.Write("second.txt", "2\n");
        await repo.GitAsync("add", "-A");
        await repo.GitAsync("commit", "-q", "-m", "second");
        repo.Write("dirty.txt", "dirty\n");

        var wt = await Manager().CreateAsync(repo.Root, "t3", baseRef: first, includeUncommitted: true);

        Assert.Equal(first, wt.BaseSha);
        Assert.False(File.Exists(Path.Combine(wt.Path, "second.txt")));
        Assert.False(File.Exists(Path.Combine(wt.Path, "dirty.txt")));
    }

    [Fact]
    public async Task Consecutive_tasks_on_a_dirty_repo_each_get_a_snapshot()
    {
        // Regression: the second snapshot used to fail once the worktree folder was in info/exclude.
        using var repo = await TestRepo.CreateAsync();
        repo.Write("README.md", "dirty\n");
        var manager = Manager();

        var first = await manager.CreateAsync(repo.Root, "a1", null, includeUncommitted: true);
        var second = await manager.CreateAsync(repo.Root, "a2", null, includeUncommitted: true);

        Assert.True(first.IncludesUncommittedChanges);
        Assert.True(second.IncludesUncommittedChanges);
        Assert.Equal("dirty\n", File.ReadAllText(Path.Combine(second.Path, "README.md")));
        Assert.False(Directory.Exists(Path.Combine(second.Path, ".orchestrator")));
    }
}
