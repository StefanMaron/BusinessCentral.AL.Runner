// NclShadowPublishDiagnosisTests — #3371: two shapes that survived #3368's fix for #3364,
// both inside NclShadowRuntime.PublishShadowDir.
//
// 1. The move's exception was discarded by empty `catch (IOException) { }` /
//    `catch (UnauthorizedAccessException) { }` handlers, and the exhaustion WARN then
//    asserted "(source locked, or sustained contention)" regardless of what was actually
//    caught. A cross-device rename, PathTooLongException, a full disk or a permission
//    problem on the shadow root all reach that WARN and are all mis-attributed to a lock
//    or contention — and the one thing that distinguished them, the exception, was gone.
//
// 2. The last `return shadowDir` was reached exactly when every attempt was exhausted AND
//    IsShadowDirComplete(tempDir) was false, and it handed back a directory whose validity
//    the `if` above had just disproved: absent entirely on the source-lock route, or
//    present-but-incomplete on the contention route. The caller re-execs into
//    Path.Combine(that, "al-runner.dll"), so the observable is the child dying with "The
//    application to execute does not exist" or hostfxr refusing a dir missing
//    al-runner.deps.json — the exact third symptom #3364 was filed for.
//
// The move is injected rather than provoked with a real lock for the same reason
// NclShadowLockedSourcePublishTests gives: a real file lock only blocks a rename on
// Windows, so a handle-based test is permanently green on the Linux CI legs.
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

// CaptureStderr swaps the process-wide Console.Error and reads the sink back, so this class
// must sit in a collection xunit will not run in parallel — ConsoleSwapIsolationGuardTests
// enforces that. The swap is load-bearing here (the WARN text IS the assertion), so dropping
// it is not the alternative it is for a class that only silences noise. This class needs no
// other serial collection: it touches no process-global state besides the console.
[Collection(ConsoleFilterSerialCollection.Name)]
public sealed class NclShadowPublishDiagnosisTests
{
    private const string MarkerFileName = ".al-runner-shadow-source";
    private const string EntryDllName = "al-runner.dll";
    private const string NclFileName = "Microsoft.Dynamics.Nav.Ncl.dll";

    private static string NewTempDir(string label)
    {
        var dir = TestScratch.FlatDir($"ncl-shadow-diag-{label}-");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WriteCompleteShadowDir(string dir, string origFull, byte[] dllBytes)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, EntryDllName), dllBytes);
        File.WriteAllBytes(Path.Combine(dir, NclFileName), new byte[] { 4, 5, 6 });
        File.WriteAllText(Path.Combine(dir, "al-runner.deps.json"), "{}");
        File.WriteAllText(Path.Combine(dir, "al-runner.runtimeconfig.json"), "{}");
        // Marker last, same invariant the real build holds.
        File.WriteAllText(Path.Combine(dir, MarkerFileName), origFull);
    }

    /// <summary>Writes a dir that passes nothing: marker present (so it is not simply
    /// "absent"), entry DLL missing — exactly the incomplete-publish shape #2489 measured,
    /// which IsShadowDirComplete must reject.</summary>
    private static void WriteIncompleteShadowDir(string dir, string origFull)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, NclFileName), new byte[] { 4, 5, 6 });
        File.WriteAllText(Path.Combine(dir, "al-runner.deps.json"), "{}");
        File.WriteAllText(Path.Combine(dir, "al-runner.runtimeconfig.json"), "{}");
        File.WriteAllText(Path.Combine(dir, MarkerFileName), origFull);
        // No EntryDllName — the file hostfxr needs most.
    }

    private static string CaptureStderr(Action body)
    {
        var prev = Console.Error;
        var sw = new StringWriter();
        Console.SetError(sw);
        try { body(); }
        finally { Console.SetError(prev); }
        return sw.ToString();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Shape 1 — the exhaustion WARN must name what was actually caught
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>The rename fails for a reason that is neither a lock nor contention — a
    /// cross-device rename, which is the field case that motivated this: the shadow root
    /// and the temp dir land on different filesystems, every one of the 20 attempts is
    /// spent on a condition that can never clear, and the operator is told to go look for
    /// an antivirus scanner. The WARN must carry the exception's own type and message, so
    /// the real cause is readable from the log alone.</summary>
    [Fact]
    public void PublishShadowDir_MoveFailsForANonLockReason_WarnNamesTheActualExceptionNotAGuessedCause()
    {
        var root = NewTempDir("crossdevice");
        try
        {
            var origFull = @"C:\install\any";
            var tempDir = Path.Combine(root, "key.building.abc");
            WriteCompleteShadowDir(tempDir, origFull, dllBytes: new byte[] { 7, 7, 7 });
            var shadowDir = Path.Combine(root, "key");

            const string realMessage = "Source and destination path must have identical roots. Move will not work across volumes.";
            string? result = null;
            var stderr = CaptureStderr(() =>
                result = NclShadowRuntime.PublishShadowDir(
                    tempDir, shadowDir, origFull,
                    move: (_, _) => throw new IOException(realMessage),
                    sleep: _ => { }));

            // The fallback itself is unchanged (#3368): run from the complete temp dir.
            Assert.Equal(tempDir, result);

            // The exception's own message must survive to the WARN — this is the whole
            // point: it is the only thing separating a cross-volume move from an AV lock.
            Assert.Contains(realMessage, stderr);
            // And its type, because two IOExceptions with different messages are two
            // different diagnoses and the type is what a reader greps for first.
            Assert.Contains(nameof(IOException), stderr);

            // The WARN must NOT assert a cause it never measured. Before this fix it said
            // exactly this, unconditionally.
            Assert.DoesNotContain("source locked, or sustained contention", stderr);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>The same claim for the other refusal mapping, and with a different
    /// exception type, so a fix that hardcodes "IOException" into the message cannot pass
    /// both. UnauthorizedAccessException on a rename whose DESTINATION root is read-only
    /// is a permission problem on the shadow root, not a transient scanner handle.</summary>
    [Fact]
    public void PublishShadowDir_MoveFailsWithUnauthorizedAccess_WarnNamesThatTypeAndMessage()
    {
        var root = NewTempDir("denied");
        try
        {
            var origFull = @"C:\install\any";
            var tempDir = Path.Combine(root, "key.building.abc");
            WriteCompleteShadowDir(tempDir, origFull, dllBytes: new byte[] { 8, 8, 8 });
            var shadowDir = Path.Combine(root, "key");

            const string realMessage = "Access to the path 'D:\\shadow-root' is denied.";
            string? result = null;
            var stderr = CaptureStderr(() =>
                result = NclShadowRuntime.PublishShadowDir(
                    tempDir, shadowDir, origFull,
                    move: (_, _) => throw new UnauthorizedAccessException(realMessage),
                    sleep: _ => { }));

            Assert.Equal(tempDir, result);
            Assert.Contains(realMessage, stderr);
            Assert.Contains(nameof(UnauthorizedAccessException), stderr);
            Assert.DoesNotContain(nameof(IOException), stderr);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>Only the LAST exception is reported, and it is the last one actually
    /// raised — not the first, which on a changing condition is the least informative.
    /// A disk that fills up mid-retry is the shape: the early attempts fail for one
    /// reason and the one the operator needs to see is what it settled into.</summary>
    [Fact]
    public void PublishShadowDir_CauseChangesAcrossAttempts_WarnReportsTheLastOneRaised()
    {
        var root = NewTempDir("changing");
        try
        {
            var origFull = @"C:\install\any";
            var tempDir = Path.Combine(root, "key.building.abc");
            WriteCompleteShadowDir(tempDir, origFull, dllBytes: new byte[] { 5, 5, 5 });
            var shadowDir = Path.Combine(root, "key");

            const string firstMessage = "The process cannot access the file because it is being used by another process.";
            const string lastMessage = "There is not enough space on the disk.";
            var attempts = 0;
            string? result = null;
            var stderr = CaptureStderr(() =>
                result = NclShadowRuntime.PublishShadowDir(
                    tempDir, shadowDir, origFull,
                    move: (_, _) =>
                    {
                        attempts++;
                        throw new IOException(attempts <= 5 ? firstMessage : lastMessage);
                    },
                    sleep: _ => { }));

            Assert.Equal(tempDir, result);
            Assert.Contains(lastMessage, stderr);
            Assert.DoesNotContain(firstMessage, stderr);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Shape 2 — the exhausted-and-incomplete path must not hand back a path the
    // condition above it just disproved
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Source-lock route, incomplete temp dir: every attempt is spent with
    /// shadowDir never created, and our own tempDir does not pass the completeness check
    /// either. There is no directory to return. Before this fix the method returned
    /// shadowDir — a path that was never created — and the caller re-exec'd into it and
    /// died with "The application to execute does not exist", the third symptom #3364 was
    /// filed for. It must throw, naming both directories and the state, rather than hand
    /// back a value it has just established is unusable.</summary>
    [Fact]
    public void PublishShadowDir_ExhaustedAndTempDirIncomplete_ThrowsInsteadOfReturningANeverCreatedPath()
    {
        var root = NewTempDir("phantom");
        try
        {
            var origFull = @"C:\install\any";
            var tempDir = Path.Combine(root, "key.building.abc");
            WriteIncompleteShadowDir(tempDir, origFull);
            var shadowDir = Path.Combine(root, "key");

            const string realMessage = "Access to the path 'x' is denied.";
            var ex = Assert.Throws<IOException>(() =>
                NclShadowRuntime.PublishShadowDir(
                    tempDir, shadowDir, origFull,
                    move: (_, _) => throw new IOException(realMessage),
                    sleep: _ => { }));

            // Names the directory the caller would otherwise have been handed...
            Assert.Contains(shadowDir, ex.Message);
            // ...the one we built and could not use...
            Assert.Contains(tempDir, ex.Message);
            // ...and the underlying refusal, so shape 1's information is not lost on the
            // one path that cannot fall back.
            Assert.Contains(realMessage, ex.ToString());

            Assert.False(Directory.Exists(shadowDir),
                "the phantom directory must still not exist — the point is that returning it was wrong");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>Contention route, incomplete published dir: shadowDir DOES exist here, so
    /// the failure mode is different and worse to diagnose — the returned path resolves,
    /// and hostfxr then refuses a directory missing al-runner.deps.json. The condition
    /// that got us here is `IsShadowDirComplete(tempDir) == false`, and shadowDir is
    /// incomplete by the loop's own exit condition, so neither is returnable.</summary>
    [Fact]
    public void PublishShadowDir_ExhaustedWithBothDirsIncomplete_ThrowsRatherThanReturningTheIncompleteShadowDir()
    {
        var root = NewTempDir("both-incomplete");
        try
        {
            var origFull = @"C:\install\any";
            var tempDir = Path.Combine(root, "key.building.abc");
            WriteIncompleteShadowDir(tempDir, origFull);
            var shadowDir = Path.Combine(root, "key");
            // A sibling published an incomplete dir; the heal cannot complete it because
            // our own source is incomplete too (the entry DLL exists nowhere).
            WriteIncompleteShadowDir(shadowDir, origFull);

            var ex = Assert.Throws<IOException>(() =>
                NclShadowRuntime.PublishShadowDir(
                    tempDir, shadowDir, origFull,
                    move: (_, _) => throw new Xunit.Sdk.XunitException("must not attempt a move onto an existing dir"),
                    sleep: _ => { }));

            Assert.Contains(shadowDir, ex.Message);
            Assert.Contains(tempDir, ex.Message);
            // The entry DLL is what is missing, and saying so is the difference between
            // "hostfxr refused something" and a diagnosis.
            Assert.Contains(EntryDllName, ex.Message);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>The throw must carry its whole diagnosis in the exception, because the
    /// caller's `finally` reaps the incomplete tempDir on the way out (it is exactly the
    /// "half-built temp dir" that block exists to clean up). A message that told the
    /// operator to go look in a directory the stack unwind has just deleted would be no
    /// better than the phantom path it replaced — so the missing-file list is a string,
    /// captured before anything is reaped, not a pointer at on-disk state.</summary>
    [Fact]
    public void PublishShadowDir_ExhaustedAndIncomplete_DiagnosisIsSelfContainedInTheMessage()
    {
        var root = NewTempDir("self-contained");
        try
        {
            var origFull = @"C:\install\any";
            var tempDir = Path.Combine(root, "key.building.abc");
            WriteIncompleteShadowDir(tempDir, origFull);
            var shadowDir = Path.Combine(root, "key");

            var ex = Assert.Throws<IOException>(() =>
                NclShadowRuntime.PublishShadowDir(
                    tempDir, shadowDir, origFull,
                    move: (_, _) => throw new IOException("Not enough space on the disk."),
                    sleep: _ => { }));

            // Simulate the caller's finally reaping the temp dir, then re-read the
            // message: everything needed to act on it must still be there.
            Directory.Delete(tempDir, recursive: true);
            Assert.Contains(EntryDllName, ex.Message);
            Assert.Contains("Not enough space on the disk.", ex.Message);
            Assert.IsType<IOException>(ex.InnerException);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>The heal loop's own failures are the same shape one level down: when the
    /// in-place heal is what could not complete, its copy exceptions were discarded too,
    /// so the exhaustion message had nothing to say about the route that actually failed.
    /// Here the heal cannot finish because the entry DLL is missing from BOTH dirs, and
    /// the message must still distinguish this from a rename that was never attempted for
    /// a different reason — it says which file is missing.</summary>
    [Fact]
    public void PublishShadowDir_HealCannotComplete_ErrorNamesTheMissingFileNotJustTheFailure()
    {
        var root = NewTempDir("heal-stuck");
        try
        {
            var origFull = @"C:\install\any";
            var tempDir = Path.Combine(root, "key.building.abc");
            WriteIncompleteShadowDir(tempDir, origFull);
            var shadowDir = Path.Combine(root, "key");
            WriteIncompleteShadowDir(shadowDir, origFull);

            var ex = Assert.Throws<IOException>(() =>
                NclShadowRuntime.PublishShadowDir(
                    tempDir, shadowDir, origFull,
                    move: (_, _) => throw new Xunit.Sdk.XunitException("destination exists; no move should be attempted"),
                    sleep: _ => { }));

            // No rename ever ran on this route, and saying so is the fact that separates
            // it from a source-side refusal — not a cause invented to fill the sentence.
            Assert.Contains("no rename was attempted", ex.Message);
            Assert.Null(ex.InnerException);
            Assert.Contains(EntryDllName, ex.Message);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>Guard against over-correcting shape 2 into a throw on the healthy
    /// fallback: when the temp dir IS complete, the exhausted path must still return it
    /// and must still not throw — that is #3368's fix for the actual #3364 crash, and
    /// this pins it against a fix that turns every exhaustion into an exception.</summary>
    [Fact]
    public void PublishShadowDir_ExhaustedButTempDirComplete_StillReturnsItWithoutThrowing()
    {
        var root = NewTempDir("still-falls-back");
        try
        {
            var origFull = @"C:\install\any";
            var tempDir = Path.Combine(root, "key.building.abc");
            WriteCompleteShadowDir(tempDir, origFull, dllBytes: new byte[] { 3, 3, 3 });
            var shadowDir = Path.Combine(root, "key");

            var result = NclShadowRuntime.PublishShadowDir(
                tempDir, shadowDir, origFull,
                move: (src, _) => throw new IOException($"Access to the path '{src}' is denied."),
                sleep: _ => { });

            Assert.Equal(tempDir, result);
            Assert.True(NclShadowRuntime.IsShadowDirComplete(result, origFull));
            Assert.Equal(new byte[] { 3, 3, 3 }, File.ReadAllBytes(Path.Combine(result, EntryDllName)));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
