// AlCacheInputsTests — #5368: the record beside an AL-output cache entry. It answers "is this entry
// still the answer for the files its compile read", and "is this DLL the one these sidecars were
// published with". Every outcome but Valid is a MISS, so each has to be reachable and distinct.
// The end-to-end proof (a deleted layout is not a HIT) is AlOutputCacheCompileInputsTests.
using System.Text.Json;
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class AlCacheInputsTests : IDisposable
{
    private readonly string _root = TestScratch.Dir("al-runner-cache-inputs-unit");
    private readonly string _record;
    private static readonly byte[] Dll = { 1, 2, 3 };
    private static readonly byte[] OtherDll = { 1, 2, 4 };

    public AlCacheInputsTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Layouts"));
        Directory.CreateDirectory(Path.Combine(_root, "Translations"));
        File.WriteAllText(Path.Combine(_root, "Layouts", "A.rdlc"), "v1");
        File.WriteAllText(Path.Combine(_root, "Translations", "a.da-DK.xlf"), "x");
        _record = Path.Combine(_root, "entry.inputs.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private static Dictionary<string, string> Artifacts(byte[]? dll = null, string enumHash = "ENUM", string? queryHash = null)
        => AlCacheInputs.Hashes(dll ?? Dll, enumHash, queryHash);

    private static string[] Reads(string root) => new[]
    {
        CompileFileReads.FilePrefix + Path.Combine(root, "Layouts", "A.rdlc"),
        CompileFileReads.FilePrefix + Path.Combine(root, "Layouts", "Late.rdlc"),   // probed, not there
        CompileFileReads.DirPrefix + Path.Combine(root, "Translations"),
        "list:|Translations/*.xlf",
    };

    private void Publish(string? root = null, byte[]? dll = null, string enumHash = "ENUM", string? queryHash = null)
    {
        root ??= _root;
        AlCacheInputs.Write(_record, root, CompileFileReads.Fingerprint(root, Reads(root)), Artifacts(dll, enumHash, queryHash));
    }

    private AlCacheInputs.Verdict Verify(string? root = null, byte[]? dll = null, string enumHash = "ENUM", string? queryHash = null)
        => AlCacheInputs.Verify(_record, root ?? _root, Artifacts(dll, enumHash, queryHash));

    [Fact]
    public void AnUnchangedTree_AndTheSameArtifacts_AreValid()
    {
        Publish();
        Assert.True(Verify().IsValid, Verify().Detail);
    }

    [Fact]
    public void AnEditedLayout_IsInputChanged_NamingIt()
    {
        Publish();
        File.WriteAllText(Path.Combine(_root, "Layouts", "A.rdlc"), "v2");

        var verdict = Verify();
        Assert.Equal(AlCacheInputs.State.InputChanged, verdict.State);
        Assert.Equal("file:Layouts/A.rdlc", verdict.Detail);
    }

    [Fact]
    public void ADeletedLayout_IsInputChanged()
    {
        Publish();
        File.Delete(Path.Combine(_root, "Layouts", "A.rdlc"));

        Assert.Equal(AlCacheInputs.State.InputChanged, Verify().State);
    }

    // The compile asked for a file that was not there and went on; it is there now.
    [Fact]
    public void AFileThatWasMissingWhenProbed_IsInputChangedOnceItExists()
    {
        Publish();
        File.WriteAllText(Path.Combine(_root, "Layouts", "Late.rdlc"), "now here");

        var verdict = Verify();
        Assert.Equal(AlCacheInputs.State.InputChanged, verdict.State);
        Assert.Equal("file:Layouts/Late.rdlc", verdict.Detail);
    }

    [Fact]
    public void ATranslationFileAddedToAListedFolder_IsInputChanged()
    {
        Publish();
        File.WriteAllText(Path.Combine(_root, "Translations", "b.de-DE.xlf"), "x");

        Assert.Equal(AlCacheInputs.State.InputChanged, Verify().State);
    }

    [Fact]
    public void ADirectoryThatDidNotExist_IsInputChangedOnceItDoes()
    {
        Directory.Delete(Path.Combine(_root, "Translations"), recursive: true);
        Publish();
        Directory.CreateDirectory(Path.Combine(_root, "Translations"));

        Assert.Equal(AlCacheInputs.State.InputChanged, Verify().State);
    }

    [Fact]
    public void AFileNoRecordedReadNames_CanChangeFreely()
    {
        Publish();
        File.WriteAllText(Path.Combine(_root, "notes.txt"), "anything");

        Assert.True(Verify().IsValid);
    }

    [Fact]
    public void ADllThatIsNotTheOneTheRecordWasPublishedWith_IsArtifactsDiffer_EvenWithEveryInputUnchanged()
    {
        Publish();

        var verdict = Verify(dll: OtherDll);
        Assert.Equal(AlCacheInputs.State.ArtifactsDiffer, verdict.State);
        Assert.Contains(".dll", verdict.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ASidecarThatIsNotTheOneTheRecordWasPublishedWith_IsArtifactsDiffer()
    {
        Publish();

        var verdict = Verify(enumHash: "ANOTHER");
        Assert.Equal(AlCacheInputs.State.ArtifactsDiffer, verdict.State);
        Assert.Contains(AlCacheSidecars.EnumRegistrySuffix, verdict.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void AQuerySidecarIsHeldToTheRecordToo_WhenTheReaderNeedsOne()
    {
        Publish(queryHash: "Q1");

        Assert.True(Verify(queryHash: "Q1").IsValid);
        Assert.Equal(AlCacheInputs.State.ArtifactsDiffer, Verify(queryHash: "Q2").State);
        // A record that never carried a query sidecar cannot vouch for one the reader is about to replay.
        Publish();
        Assert.Equal(AlCacheInputs.State.ArtifactsDiffer, Verify(queryHash: "Q1").State);
    }

    [Fact]
    public void ARecordWithNoInputs_IsValid_WhenTheArtifactsMatch()
    {
        AlCacheInputs.Write(_record, _root, null, Artifacts());

        Assert.True(Verify().IsValid);
        Assert.Equal(AlCacheInputs.State.ArtifactsDiffer, Verify(dll: OtherDll).State);
    }

    [Fact]
    public void TheRecordNamesPathsRelativeToTheAppRoot_SoAMovedTreeStillAnswers()
    {
        Publish();
        Assert.DoesNotContain(_root, File.ReadAllText(_record), StringComparison.Ordinal);

        var moved = TestScratch.Dir("al-runner-cache-inputs-unit-moved");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(moved)!);
            CopyTree(_root, moved);

            Assert.True(Verify(root: moved).IsValid, Verify(root: moved).Detail);
            File.WriteAllText(Path.Combine(moved, "Layouts", "A.rdlc"), "v2");
            Assert.Equal(AlCacheInputs.State.InputChanged, Verify(root: moved).State);
        }
        finally
        {
            try { Directory.Delete(moved, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    // An input outside the app root has no portable name, so it is kept as the path it was read at.
    [Fact]
    public void AnInputOutsideTheAppRoot_IsKeptAbsolute_AndStillChecked()
    {
        var outside = TestScratch.Dir("al-runner-cache-inputs-unit-outside");
        Directory.CreateDirectory(outside);
        var shared = Path.Combine(outside, "shared.rdlc");
        File.WriteAllText(shared, "v1");
        try
        {
            var key = CompileFileReads.FilePrefix + shared;
            AlCacheInputs.Write(_record, _root, CompileFileReads.Fingerprint(_root, new[] { key }), Artifacts());
            Assert.Contains(shared.Replace('\\', '/'), File.ReadAllText(_record).Replace("\\\\", "/"), StringComparison.Ordinal);
            Assert.True(Verify().IsValid);

            File.WriteAllText(shared, "v2");
            Assert.Equal(AlCacheInputs.State.InputChanged, Verify().State);
        }
        finally
        {
            try { Directory.Delete(outside, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void ARecordThatCannotBeRead_IsUnreadable_NeverValid()
    {
        File.WriteAllText(_record, "{ not json");
        Assert.Equal(AlCacheInputs.State.Unreadable, Verify().State);

        File.WriteAllText(_record, "{}");   // a format-0 record: nothing vouches for anything
        Assert.Equal(AlCacheInputs.State.Unreadable, Verify().State);

        File.WriteAllText(_record, "null");
        Assert.Equal(AlCacheInputs.State.Unreadable, Verify().State);

        File.Delete(_record);
        Assert.Equal(AlCacheInputs.State.Unreadable, Verify().State);
    }

    [Fact]
    public void ARecordNamingAKindOfInputThisRunnerDoesNotKnow_IsUnreadable()
    {
        Publish();
        var doc = JsonDocument.Parse(File.ReadAllText(_record)).RootElement;
        var tampered = new Dictionary<string, object>
        {
            ["Format"] = doc.GetProperty("Format").GetInt32(),
            ["Artifacts"] = JsonSerializer.Deserialize<Dictionary<string, string>>(doc.GetProperty("Artifacts").GetRawText())!,
            ["Inputs"] = new Dictionary<string, string> { ["socket:/tmp/x"] = "1" },
        };
        File.WriteAllText(_record, JsonSerializer.Serialize(tampered));

        Assert.Equal(AlCacheInputs.State.Unreadable, Verify().State);
    }

    [Fact]
    public void ARecordNamingInputs_CannotBeChecked_WithNoAppRoot()
    {
        Publish();

        Assert.Equal(AlCacheInputs.State.Unreadable, AlCacheInputs.Verify(_record, null, Artifacts()).State);
    }

    [Fact]
    public void VerifyEntry_HashesTheDllAndTheSidecarsItIsHanded()
    {
        var enumPath = Path.Combine(_root, "entry.enum-registry.json");
        var queryPath = Path.Combine(_root, "entry.query-symbols.json");
        File.WriteAllText(enumPath, "enums");
        File.WriteAllText(queryPath, "query");
        AlCacheInputs.Write(_record, _root, null,
            AlCacheInputs.Hashes(Dll, AlCacheInputs.HashFile(enumPath), AlCacheInputs.HashFile(queryPath)));

        Assert.True(AlCacheInputs.VerifyEntry(_record, _root, Dll, enumPath, queryPath).IsValid);
        Assert.True(AlCacheInputs.VerifyEntry(_record, _root, Dll, enumPath, null).IsValid);

        File.WriteAllText(enumPath, "enums, written by another compile");
        Assert.Equal(AlCacheInputs.State.ArtifactsDiffer, AlCacheInputs.VerifyEntry(_record, _root, Dll, enumPath, null).State);
        File.WriteAllText(enumPath, "enums");
        File.WriteAllText(queryPath, "query, written by another compile");
        Assert.Equal(AlCacheInputs.State.ArtifactsDiffer, AlCacheInputs.VerifyEntry(_record, _root, Dll, enumPath, queryPath).State);
        File.Delete(enumPath);
        Assert.Equal(AlCacheInputs.State.Unreadable, AlCacheInputs.VerifyEntry(_record, _root, Dll, enumPath, null).State);
    }

    private static void CopyTree(string from, string to)
    {
        foreach (var dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)), overwrite: true);
    }
}
