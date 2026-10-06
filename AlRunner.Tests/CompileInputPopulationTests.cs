// CompileInputPopulationTests — #5087: the population of non-.al files the change model hashes is
// whatever BC's compiler reads through its IFileSystem. These pin that population at its two seams:
// every member of the interface is a recorded read or is named as not one, and every compile that
// attaches a file system gets the recording one. A member BC adds to IFileSystem, or a new
// WithFileSystem site, fails here instead of becoming an input nothing hashes.
// Rules: docs/server-mode.md#affectedonly-and-files-the-compile-reads.
using Xunit;
using NavCA = Microsoft.Dynamics.Nav.CodeAnalysis;

namespace AlRunner.Tests;

public sealed class CompileInputPopulationTests : IDisposable
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private readonly string _root = TestScratch.Dir("al-runner-compile-input-population");

    public CompileInputPopulationTests() => Directory.CreateDirectory(Path.Combine(_root, "sub"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    // What each READ member does when called, and how to change what it would answer next time.
    private static readonly (string Member, Action<NavCA.IFileSystem, string> Call, Action<string> Change)[] Reads =
    {
        ("ReadBytes", (fs, _) => fs.ReadBytes("sub/f.bin"), root => File.WriteAllText(Path.Combine(root, "sub", "f.bin"), "changed")),
        ("ReadBytes", (fs, _) => fs.ReadBytes("sub/f.bin", 1), root => File.WriteAllText(Path.Combine(root, "sub", "f.bin"), "changed")),
        ("Exists", (fs, _) => fs.Exists("sub/f.bin"), root => File.WriteAllText(Path.Combine(root, "sub", "f.bin"), "changed")),
        ("OpenRead", (fs, _) => fs.OpenRead("sub/f.bin").Dispose(), root => File.WriteAllText(Path.Combine(root, "sub", "f.bin"), "changed")),
        ("OpenFile", (fs, root) => fs.OpenFile(Path.Combine(root, "sub", "f.bin"), FileMode.Open, FileAccess.Read, FileShare.Read).Dispose(),
            root => File.WriteAllText(Path.Combine(root, "sub", "f.bin"), "changed")),
        ("GetFileSize", (fs, root) => fs.GetFileSize(Path.Combine(root, "sub", "f.bin")), root => File.WriteAllText(Path.Combine(root, "sub", "f.bin"), "changed")),
        ("GetFiles", (fs, _) => fs.GetFiles("sub/*.x").ToList(), root => File.WriteAllText(Path.Combine(root, "sub", "new.x"), "x")),
        ("GetFiles", (fs, _) => fs.GetFiles("sub", "*.x").ToList(), root => File.WriteAllText(Path.Combine(root, "sub", "new.x"), "x")),
        ("GetFilesRecursively", (fs, _) => fs.GetFilesRecursively("sub").ToList(), root => File.WriteAllText(Path.Combine(root, "sub", "new.x"), "x")),
        ("DirectoryExists", (fs, _) => fs.DirectoryExists("sub"), root => Directory.Delete(Path.Combine(root, "sub"), true)),
    };

    // Members that neither read a file's content nor list a tree. Adding to this list is a claim.
    private static readonly string[] NotReads =
    {
        "WriteBytes", "DirectoryExistsForFile", "CreateDirectoryForFile", "CreateFile", "OpenWrite",
        "DeleteFile", "GetDirectoryPath", "GetAbsolutePath",
    };

    [Fact]
    public void EveryMemberOfBcsFileSystemInterface_IsARecordedRead_OrNamedAsNotOne()
    {
        var members = typeof(NavCA.IFileSystem).GetMethods().Select(m => m.Name).Distinct().OrderBy(n => n, StringComparer.Ordinal).ToArray();
        var classified = Reads.Select(r => r.Member).Concat(NotReads).Distinct().OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(classified, members);
    }

    [Fact]
    public void EachReadMember_RecordsAnInputWhoseFingerprintMovesWhenWhatItReadMoves()
    {
        foreach (var (member, call, change) in Reads)
        {
            var root = TestScratch.Dir("al-runner-compile-input-population-read");
            Directory.CreateDirectory(Path.Combine(root, "sub"));
            File.WriteAllText(Path.Combine(root, "sub", "f.bin"), "original");
            var reads = new CompileFileReads();
            var fs = reads.Wrap(new NavCA.RelativeFileSystem(root));

            call(fs, root);

            var keys = reads.Keys;
            Assert.True(keys.Count > 0, $"{member} recorded nothing");
            var before = CompileFileReads.Fingerprint(root, keys);
            change(root);
            var after = CompileFileReads.Fingerprint(root, keys);
            Assert.True(keys.Any(k => before[k] != after[k]), $"{member}: no recorded input moved when what it read did");
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void AFileThatDidNotExistWhenProbed_IsAChangeWhenItAppears()
    {
        var reads = new CompileFileReads();
        var fs = reads.Wrap(new NavCA.RelativeFileSystem(_root));

        Assert.False(fs.Exists("sub/late.rdlc"));
        var before = CompileFileReads.Fingerprint(_root, reads.Keys);
        File.WriteAllText(Path.Combine(_root, "sub", "late.rdlc"), "now here");
        var after = CompileFileReads.Fingerprint(_root, reads.Keys);

        Assert.NotEqual(before.Values.OrderBy(v => v), after.Values.OrderBy(v => v));
    }

    [Fact]
    public void EveryCompileThatAttachesAFileSystem_GetsTheRecordingOne()
    {
        var sources = Directory.GetFiles(Path.Combine(RepoRoot, "AlRunner"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .ToDictionary(f => Path.GetRelativePath(RepoRoot, f), File.ReadAllText);

        // Only these two construct BC's file system; everything else gets one from Build.
        var constructors = sources.Where(kv => kv.Value.Contains("new NavCA.RelativeFileSystem(", StringComparison.Ordinal))
            .Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { Path.Combine("AlRunner", "CompileFileReads.cs"), Path.Combine("AlRunner", "ReportLayoutFileSystem.cs") }, constructors);

        // Each file system a compilation is given comes from Build, and Build is handed a recorder.
        var buildCalls = sources.SelectMany(kv => System.Text.RegularExpressions.Regex
                .Matches(kv.Value, @"ReportLayoutFileSystem\.Build\(([^;]*)\);").Select(m => (File: kv.Key, Args: m.Groups[1].Value)))
            .ToList();
        Assert.NotEmpty(buildCalls);
        Assert.All(buildCalls, c => Assert.True(
            c.Args.TrimEnd().EndsWith("Reads", StringComparison.Ordinal),
            $"{c.File}: ReportLayoutFileSystem.Build({c.Args}) is not handed a CompileFileReads, so what that compile reads is not hashed"));

        var attached = sources.SelectMany(kv => System.Text.RegularExpressions.Regex
                .Matches(kv.Value, @"\.WithFileSystem\(([A-Za-z_]+)\)").Select(m => m.Groups[1].Value))
            .Distinct().OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "compileFileSystem", "depCompileFileSystem", "radFileSystem" }, attached);
    }
}
