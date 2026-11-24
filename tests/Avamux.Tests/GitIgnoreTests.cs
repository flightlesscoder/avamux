using Avamux.Core;
using Avamux.Core.Config;
using Avamux.Core.Repos;

namespace Avamux.Tests;

public class GitIgnoreTests
{
    [Fact]
    public void Patterns_match_one_path_and_suggestions_cover_file_type_and_folder()
    {
        Assert.Equal("/Daily/notes.md", GitIgnore.PatternFor("Daily/notes.md"));
        Assert.Equal("/odd\\[1\\]\\*.txt", GitIgnore.PatternFor("odd[1]*.txt"));
        Assert.Equal("/\\#scratch.md", GitIgnore.PatternFor("#scratch.md"));
        Assert.Equal(
            [("This File (big.zip)", "/files/big.zip"), ("All .zip Files", "*.zip"), ("The Folder files/", "/files/")],
            GitIgnore.Suggestions("files/big.zip"));
        Assert.Equal([("This File (README)", "/README")], GitIgnore.Suggestions("README"));
    }

    [Fact]
    public void Adding_a_pattern_once_and_the_default_only_when_there_is_none()
    {
        using var box = new Sandbox();
        var dir = box.Work("repo");
        Directory.CreateDirectory(dir);
        Assert.True(GitIgnore.WriteDefault(dir, GitIgnore.WikiDefault));
        Assert.False(GitIgnore.WriteDefault(dir, "other"));
        var text = File.ReadAllText(GitIgnore.FileOf(dir));
        Assert.Contains("\n.DS_Store\n", text);
        Assert.Contains("\n*.swp\n", text);
        Assert.DoesNotContain("\r", text);
        Assert.StartsWith("# Written by avamux", text); // no leading indentation from the source

        File.WriteAllText(GitIgnore.FileOf(dir), "a.txt"); // no newline at the end
        Assert.True(GitIgnore.Add(dir, "*.zip"));
        Assert.False(GitIgnore.Add(dir, "*.zip"));
        Assert.Equal("a.txt\n*.zip\n", File.ReadAllText(GitIgnore.FileOf(dir)));
    }

    [Fact]
    public async Task Big_and_binary_files_are_found_and_ignoring_a_tracked_one_takes_it_out_of_git()
    {
        using var box = new Sandbox();
        var dir = box.Work("wiki");
        Directory.CreateDirectory(dir);
        async Task<string> Git(params string[] args) { var r = await Shell.RunAsync("git", args, dir); Assert.True(r.ExitCode == 0, r.Stderr + r.Stdout); return r.Stdout.Trim(); }
        using var config = new ConfigService(box.Paths);
        var repos = new RepoService(box.Paths, config);
        var repo = new RepoEntry { Id = "w", Name = "wiki", Path = dir };
        await repos.InitAsync(repo, GitIgnore.WikiDefault);
        Assert.True(File.Exists(GitIgnore.FileOf(dir)));
        await Git("config", "user.email", "t@example.com");
        await Git("config", "user.name", "T");
        await Git("config", "commit.gpgsign", "false");

        File.WriteAllText(Path.Combine(dir, "page.md"), "# Page\n");
        File.WriteAllBytes(Path.Combine(dir, "photo.png"), [0x89, 0x50, 0x4E, 0x47, 0, 0, 0, 1]); // binary, but a picture
        File.WriteAllBytes(Path.Combine(dir, "tool.bin"), [1, 2, 0, 3]);
        File.WriteAllText(Path.Combine(dir, "huge.txt"), new string('x', 3 * 1024 * 1024));
        File.WriteAllText(Path.Combine(dir, "notes.swp"), "editor scratch");
        var status = await repos.StatusAsync(repo);
        Assert.DoesNotContain(status.Changes, c => c.Path == "notes.swp"); // the default .gitignore at work
        var heavy = RepoService.HeavyFiles(repo, status.Changes.Select(c => c.Path), 2 * 1024 * 1024);
        Assert.Equal(["huge.txt", "tool.bin"], heavy.Select(h => h.Path).Order());
        Assert.Equal("3 MB", heavy.Single(h => h.Path == "huge.txt").Why);
        Assert.Equal("binary", heavy.Single(h => h.Path == "tool.bin").Why);

        await Git("add", "-A");
        await Git("commit", "-qm", "first");
        await repos.IgnoreAsync(repo, GitIgnore.PatternFor("tool.bin"), ["tool.bin"]);
        Assert.True(File.Exists(Path.Combine(dir, "tool.bin"))); // still on disk
        Assert.Equal("", await Git("ls-files", "--", "tool.bin")); // but out of git
        Assert.Contains("/tool.bin", File.ReadAllText(GitIgnore.FileOf(dir)));
        status = await repos.StatusAsync(repo);
        Assert.Contains(status.Changes, c => c.Path == "tool.bin" && c.Staged && c.Description == "deleted");
    }
}
