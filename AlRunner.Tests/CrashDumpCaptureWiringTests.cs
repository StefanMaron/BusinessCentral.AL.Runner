// CrashDumpCaptureWiringTests — issue #2819: a corpus run died with SIGSEGV (exit 139) seconds
// in, before any test reported. No stack, no managed exception, nothing to read. One occurrence,
// not reproduced in four further runs of the same tree.
//
// The wiring this guards is worth a test for the same reason the workflow guards already here
// are: it is invisible until the rare moment it matters, and by then a silent regression has
// already cost the one occurrence it existed to capture. Every assertion below corresponds to a
// way the capture can be present-but-useless.
//
// Verified by hand on .NET 8 before writing this, rather than assumed from documentation:
//
//   * a REAL SIGSEGV (kill -SEGV against a live dotnet process) is caught — createdump reports
//     `Crashing thread ... signal 11 (000b)` and writes the file. That is the exact signal
//     #2819 reports; a managed AccessViolationException arrives as signal 6 instead, so testing
//     only that shape would have proven the wrong thing.
//   * createdump does NOT create the directory in DOTNET_DbgMiniDumpName's path. Without the
//     mkdir it fails to write, and says so only on the stderr of a process that is already
//     dying.
//   * type 2 on a trivial hello-world process is already ~127 MB, and it scales with committed
//     memory. Type 4 (full memory) against a runner with BC loaded is a multi-GB file on a
//     hosted runner with roughly 14 GB free — which would convert a rare crash into a reliable
//     out-of-disk failure. That is why this asserts 2 and not the 4 the issue suggested.
using System.Text.RegularExpressions;
using Xunit;

namespace AlRunner.Tests;

public sealed class CrashDumpCaptureWiringTests
{
    private static readonly string WorkflowDir = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".github", "workflows"));

    private static string Read(string name)
    {
        var path = Path.Combine(WorkflowDir, name);
        Assert.True(File.Exists(path), $"expected workflow {name} at {path}");
        return File.ReadAllText(path);
    }

    private static string CodeOnly(string text) =>
        string.Join('\n', text.Split('\n').Where(l => !l.TrimStart().StartsWith('#')));

    [Fact]
    public void BcTests_EnablesMiniDumpCapture()
    {
        var code = CodeOnly(Read("bc-tests.yml"));

        Assert.Matches(new Regex(@"DOTNET_DbgEnableMiniDump:\s*'?1'?"), code);
        Assert.Matches(new Regex(@"DOTNET_DbgMiniDumpName:.*crash-dumps"), code);
    }

    /// <summary>Type 2, not 4. A full-memory dump of a runner with BC loaded is measured in GB
    /// against roughly 14 GB of free disk on a hosted runner, so 4 would trade a rare crash for
    /// a reliable infrastructure failure. Asserted as an exact value because "some dump type is
    /// configured" is precisely the check that would let 4 back in unnoticed.</summary>
    [Fact]
    public void BcTests_UsesTheHeapDumpType_NotFullMemory()
    {
        var code = CodeOnly(Read("bc-tests.yml"));

        Assert.Matches(new Regex(@"DOTNET_DbgMiniDumpType:\s*'?2'?"), code);
        Assert.DoesNotMatch(new Regex(@"DOTNET_DbgMiniDumpType:\s*'?4'?"), code);
    }

    /// <summary>createdump does not create the directory it is told to write into. Without this
    /// step the dump is silently never written, and the only sign is a line on the stderr of an
    /// already-dying process — which is the same nothing-to-read situation #2819 is about.</summary>
    [Fact]
    public void BcTests_CreatesTheDumpDirectoryBeforeAnythingCanCrash()
    {
        var code = CodeOnly(Read("bc-tests.yml"));

        Assert.Matches(new Regex(@"mkdir -p .*crash-dumps"), code);

        // Ordering matters as much as presence: a mkdir after the runner has already been
        // invoked cannot help the invocation that crashed.
        var mkdirAt = code.IndexOf("mkdir -p", StringComparison.Ordinal);
        var firstRunnerAt = code.IndexOf("dotnet run --no-build --project AlRunner", StringComparison.Ordinal);
        Assert.True(mkdirAt >= 0 && firstRunnerAt >= 0 && mkdirAt < firstRunnerAt,
            "the crash-dump directory must be created before the first runner invocation, or the "
            + "first crash — the one worth capturing — writes nothing");
    }

    /// <summary>A dump written into the workspace and never uploaded is discarded when the runner
    /// is torn down, which is indistinguishable from never having captured it.</summary>
    [Fact]
    public void BcTests_UploadsAnyDumpItProduces()
    {
        var code = CodeOnly(Read("bc-tests.yml"));

        Assert.Matches(new Regex(@"name:\s*crash-dumps-\$\{\{\s*matrix\.target\.bc-version\s*\}\}"), code);
        Assert.Matches(new Regex(@"path:\s*crash-dumps/"), code);

        // Without this the step FAILS on every green run, since no dump exists — turning a
        // diagnostic aid into a permanent red leg.
        Assert.Matches(new Regex(@"if-no-files-found:\s*ignore"), code);
    }

    /// <summary>The upload must not be conditional on the runner step's success in a way that
    /// skips it on the crash — `always()` is the only condition that fires when a step died.</summary>
    [Fact]
    public void BcTests_UploadsDumpsEvenWhenTheLegFailed()
    {
        var code = CodeOnly(Read("bc-tests.yml"));
        var uploadAt = code.IndexOf("crash-dumps-${{ matrix.target.bc-version }}", StringComparison.Ordinal);
        Assert.True(uploadAt > 0, "the crash-dump upload step is missing");

        // The `if:` governing that step is the nearest one above it.
        var before = code[..uploadAt];
        var lastIf = before.LastIndexOf("if:", StringComparison.Ordinal);
        Assert.True(lastIf > 0, "the crash-dump upload step has no `if:` condition");
        var condition = before[lastIf..];
        Assert.Contains("always()", condition, StringComparison.Ordinal);
    }
}
