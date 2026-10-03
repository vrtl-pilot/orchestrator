using Orchestrator.Core.Git;

namespace Orchestrator.Tests;

/// <summary>A throwaway Git repository with one commit.</summary>
internal sealed class TestRepo : IDisposable
{
    public string Root { get; }

    private TestRepo(string root) => Root = root;

    public static async Task<TestRepo> CreateAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "orch-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        var repo = new TestRepo(root);
        await repo.GitAsync("init", "-q", "-b", "main");
        await repo.GitAsync("config", "user.email", "test@example.com");
        await repo.GitAsync("config", "user.name", "Test");
        await repo.GitAsync("config", "commit.gpgsign", "false");
        repo.Write("README.md", "hello\n");
        await repo.GitAsync("add", "-A");
        await repo.GitAsync("commit", "-q", "-m", "initial");
        return repo;
    }

    public void Write(string relative, string content)
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public Task<string> GitAsync(params string[] args) => GitClient.RunCheckedAsync(Root, args);

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
