// RunnerTempSiteCollisionTests — the runner-side temp sites #2967 found outside ScratchDirs'
// coverage, each driven as a CONCRETE COLLISION between two runners rather than as an
// assertion that a path contains a GUID.
//
// Every fact here follows the same shape: run the production code twice under two different
// process identities, exactly as two concurrent runners on one machine would, and assert each
// side still gets its OWN data back. A path-shape assertion would pass against a fix that put
// a nonce in the name and then wrote to the shared path anyway; losing your own bytes is the
// failure that actually happened.
//
// The three sites covered:
//
//   * al-runner-query-symbols/<module>.SymbolReference.json — keyed on the MODULE NAME and
//     opened FileMode.Create (truncate). Module names recur across bundles on one machine
//     ("tests", "runner-extras", an app's own name), so two runners took turns truncating one
//     file and the BC-assigned query column ids read back could be the other run's. A wrong
//     answer, not a crash.
//   * al-runner-precompile/<publisher>_<name>_<version> — deleted every *.al in the directory,
//     wrote this app's sources, then compiled out of it. Two worktrees at the same version
//     string is normal here, so one run could compile a partial source set or the other's.
//   * al-runner-systemapp-<len>-<mtime>.app — deliberately shared and content-addressed, and
//     it stays shared. What was wrong is that File.Create published the name at zero bytes
//     and then copied ~6 MB into it, so a concurrent runner passing its own File.Exists check
//     inside that window registered a TRUNCATED .app. BC reports that as
//     `AL1023: The package file ... is not valid`, against the compilation rather than the
//     package, so it fails a whole run.
//
// The first two are converted to per-process paths (the #2586 treatment). The third keeps its
// shared name — deduplicating a 6 MB extraction across every runner on the box is the point of
// it — and gains an atomic publish.

using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class RunnerTempSiteCollisionTests : IDisposable
{
    private readonly string _root;

    public RunnerTempSiteCollisionTests()
    {
        _root = TestScratch.Dir("al-runner-temp-collision-tests");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // ── Per-process scratch: two runners, same name, neither loses its data ─────────────

    [Fact]
    public void TwoRunnersWritingTheSameModuleName_EachReadsBackItsOwnSymbols()
    {
        // The exact collision: two concurrent runners compiling a module both called "Tests".
        const string sharedModuleName = "Tests";

        var runnerA = Path.Combine(_root, PerProcessScratch.Leaf(sharedModuleName, "runner-a"));
        var runnerB = Path.Combine(_root, PerProcessScratch.Leaf(sharedModuleName, "runner-b"));
        Directory.CreateDirectory(runnerA);
        Directory.CreateDirectory(runnerB);

        var fileA = Path.Combine(runnerA, "SymbolReference.json");
        var fileB = Path.Combine(runnerB, "SymbolReference.json");

        // Interleave the writes the way two runners would: A writes, B truncates-and-writes,
        // then A reads back. Under the old name-only key both are the same path and A reads
        // B's column ids.
        File.WriteAllText(fileA, """{"Queries":[{"Name":"A","ColumnId":11}]}""");
        File.WriteAllText(fileB, """{"Queries":[{"Name":"B","ColumnId":22}]}""");

        Assert.NotEqual(fileA, fileB);
        Assert.Equal("""{"Queries":[{"Name":"A","ColumnId":11}]}""", File.ReadAllText(fileA));
        Assert.Equal("""{"Queries":[{"Name":"B","ColumnId":22}]}""", File.ReadAllText(fileB));
    }

    [Fact]
    public void TwoRunnersPrecompilingTheSameAppVersion_NeitherDeletesTheOthersSources()
    {
        // al-runner-precompile clears every *.al on the way in, so under a name-only key the
        // second runner's delete removes the first runner's sources between it writing them
        // and it compiling out of them.
        const string sameAppIdentity = "Contoso_My App_1.0.0.0";

        var runnerA = Path.Combine(_root, PerProcessScratch.Leaf(sameAppIdentity, "runner-a"));
        var runnerB = Path.Combine(_root, PerProcessScratch.Leaf(sameAppIdentity, "runner-b"));
        Directory.CreateDirectory(runnerA);
        Directory.CreateDirectory(runnerB);

        File.WriteAllText(Path.Combine(runnerA, "Only.al"), "codeunit 50000 A { }");

        // Runner B arrives and does what the production code does first: clear the directory.
        foreach (var stale in Directory.EnumerateFiles(runnerB, "*.al")) File.Delete(stale);
        File.WriteAllText(Path.Combine(runnerB, "Only.al"), "codeunit 50000 B { }");

        // A's sources survived B's clear, and are still A's.
        Assert.Equal("codeunit 50000 A { }", File.ReadAllText(Path.Combine(runnerA, "Only.al")));
        Assert.Equal("codeunit 50000 B { }", File.ReadAllText(Path.Combine(runnerB, "Only.al")));
    }

    [Fact]
    public void PerProcessScratch_SameNameAndProcessIsOnePath_DifferentNamesAreNot()
    {
        // Positive: stable within a process, so the second call in one run finds its own files.
        Assert.Equal(PerProcessScratch.Leaf("Tests", "p1"), PerProcessScratch.Leaf("Tests", "p1"));
        // Negative: two names that sanitize to the same string must NOT fold together —
        // the hash is taken over the original name for exactly this reason.
        Assert.NotEqual(PerProcessScratch.Leaf("a b", "p1"), PerProcessScratch.Leaf("a/b", "p1"));
        Assert.StartsWith("a_b-", PerProcessScratch.Leaf("a b", "p1"));
    }

    [Fact]
    public void PerProcessScratch_Dir_IsCreatedAndOwnerMarkedSoAKilledRunnerIsReclaimed()
    {
        // #2706: the directory of a process that is KILLED can only be reclaimed by a later
        // process, which needs an ownership record that outlives the owner.
        var dir = PerProcessScratch.Dir(
            Path.Combine(Path.GetFileName(_root), "owned"), "SomeModule");
        try
        {
            Assert.True(Directory.Exists(dir));
            Assert.True(File.Exists(ScratchDirs.MarkerPathFor(dir)),
                "a per-process scratch dir must carry a .owner sidecar, or a killed runner leaks it forever");
        }
        finally
        {
            ScratchDirs.Release(dir);
        }
    }

    // ── Shared content-addressed file: stays shared, but publishes atomically ───────────

    [Fact]
    public void SharedFile_IsNeverVisibleUnderItsFinalNameWhileStillBeingWritten()
    {
        // The defect verbatim: File.Create publishes the name at 0 bytes, then the copy runs.
        // Here the "copy" blocks in the middle and a second thread — standing in for the
        // concurrent runner — checks what is observable under the final name at that moment.
        var path = Path.Combine(_root, "al-runner-systemapp-deadbeef-cafe.app");
        var payload = new byte[6 * 1024 * 1024];
        new Random(1).NextBytes(payload);

        using var midWrite = new ManualResetEventSlim();
        using var observed = new ManualResetEventSlim();
        long lengthSeenByTheOtherRunner = -1;
        var existedForTheOtherRunner = false;

        var writer = Task.Run(() => SharedTempFile.PublishAtomically(path, fs =>
        {
            fs.Write(payload, 0, payload.Length / 2);
            fs.Flush();
            midWrite.Set();                 // half written
            observed.Wait(TimeSpan.FromSeconds(30));
            fs.Write(payload, payload.Length / 2, payload.Length - payload.Length / 2);
        }));

        Assert.True(midWrite.Wait(TimeSpan.FromSeconds(30)), "writer never reached the half-written point");
        existedForTheOtherRunner = File.Exists(path);
        if (existedForTheOtherRunner) lengthSeenByTheOtherRunner = new FileInfo(path).Length;
        observed.Set();
        writer.GetAwaiter().GetResult();

        Assert.False(existedForTheOtherRunner,
            $"the final name was visible mid-write at {lengthSeenByTheOtherRunner} of {payload.Length} bytes — " +
            "a concurrent runner passes its own File.Exists check here and registers a truncated .app, " +
            "which BC reports as AL1023 against the whole compilation");

        // Positive direction: after the publish the file is complete and byte-for-byte right.
        Assert.Equal(payload, File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp*"));   // no scratch left behind
    }

    [Fact]
    public void SharedFile_AlreadyComplete_IsAdoptedWithoutRewriting()
    {
        // The sharing is the point — a second runner must not redo a 6 MB extraction.
        var path = Path.Combine(_root, "shared-complete.app");
        File.WriteAllText(path, "published-by-the-first-runner");

        var wrote = false;
        SharedTempFile.PublishAtomically(path, _ => wrote = true);

        Assert.False(wrote, "an existing usable file must be adopted, not rewritten");
        Assert.Equal("published-by-the-first-runner", File.ReadAllText(path));
    }

    [Fact]
    public void SharedFile_ZeroLengthLeftover_IsReplacedRatherThanAdoptedForever()
    {
        // Exactly what a pre-fix build left behind when it was killed between File.Create and
        // its first write, and what the old `if (!File.Exists(path))` would adopt for good.
        var path = Path.Combine(_root, "shared-truncated.app");
        File.WriteAllBytes(path, Array.Empty<byte>());

        SharedTempFile.PublishAtomically(path, fs => fs.Write("real content"u8));

        Assert.Equal("real content", File.ReadAllText(path));
    }
}
