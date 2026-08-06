using System;
using System.IO;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Issue #1651: on Linux, when Win32Stubs can't build/load its P/Invoke shim (e.g. no C
/// compiler on PATH), the resolver used to swallow the exception and return IntPtr.Zero.
/// .NET's own DllImportResolver fallback then took over, producing a
/// <c>DllNotFoundException: kernel32.dll.so not found</c> hundreds of frames away from the
/// real cause — inside <c>WindowsLanguageHelper..cctor</c>, itself triggered by an install
/// codeunit touching an ordinary <c>TextConstant</c> (e.g. via System App's
/// <c>Upgrade Tag.SetUpgradeTag</c>). Reproduced end-to-end manually against this repo by
/// running al-runner with PATH stripped of cc/gcc/clang: exit 3, 0 tests, and — critically —
/// the one line that would have explained why (<c>[Win32Stubs] build failed for …</c>) was
/// ALSO invisible by default, because it matched Log's generic `[Component]` suppression
/// regex (Log.cs) and none of `[bc]`/`[dep]`/`[layered]`/`[provision]`/`[watch]` cover it.
///
/// Fix: the resolver no longer catches-and-defaults; it lets the real exception propagate,
/// with a message that names the missing library, lists every compiler tried, and gives two
/// concrete remediations. These tests pin the pure, injectable pieces of that message and
/// compiler-selection logic so the content can't silently regress into something vague again.
/// </summary>
public class Win32StubsLoudFailureTests
{
    [Fact]
    public void FindCompiler_ReturnsFirstAvailableCandidate_InOrder()
    {
        // cc missing, gcc present — gcc must win even though clang is also present,
        // because CandidateCompilers is tried in order.
        var found = Win32Stubs.FindCompiler(cmd => cmd is "gcc" or "clang");
        Assert.Equal("gcc", found);
    }

    [Fact]
    public void FindCompiler_ReturnsNull_WhenNoCandidateExists()
    {
        var found = Win32Stubs.FindCompiler(_ => false);
        Assert.Null(found);
    }

    [Fact]
    public void CandidateCompilers_TriesCcFirst_ThenGccThenClang()
    {
        // Pinned so a reorder doesn't silently change which compiler wins when several
        // are installed (cc is the POSIX-mandated name and should be preferred).
        Assert.Equal(new[] { "cc", "gcc", "clang" }, Win32Stubs.CandidateCompilers);
    }

    [Fact]
    public void BuildNoCompilerMessage_NamesTheFailingLibrary()
    {
        var msg = Win32Stubs.BuildNoCompilerMessage("kernel32.dll");
        Assert.Contains("kernel32.dll", msg);
    }

    [Fact]
    public void BuildNoCompilerMessage_ListsEveryCandidateCompilerTried()
    {
        var msg = Win32Stubs.BuildNoCompilerMessage("kernel32.dll");
        foreach (var c in Win32Stubs.CandidateCompilers)
            Assert.Contains(c, msg);
    }

    /// <summary>
    /// Would a message that always says the same generic "something went wrong" pass this
    /// test? No — it must name the *specific* remediation of setting the override env var,
    /// not just "check your setup". This is the assertion that would catch a regression back
    /// to a vague message.
    /// </summary>
    [Fact]
    public void BuildNoCompilerMessage_NamesTheOverrideEnvVar()
    {
        var msg = Win32Stubs.BuildNoCompilerMessage("kernel32.dll");
        Assert.Contains("AL_RUNNER_WIN32_STUBS_SO", msg);
    }

    [Fact]
    public void BuildNoCompilerMessage_ReferencesTheTrackingIssue()
    {
        var msg = Win32Stubs.BuildNoCompilerMessage("user32.dll");
        Assert.Contains("1651", msg);
    }

    /// <summary>
    /// GREEN: AL_RUNNER_WIN32_STUBS_SO pointing at a real, loadable shared library must be
    /// honoured — GetOrBuild loads it directly, with zero process invocations (no cc needed
    /// at all). Builds a tiny valid ELF .so with the real cc (available in this dev/CI
    /// environment) purely as test fixture data; the assertion is that Win32Stubs picks it
    /// up via the env var, not that this specific test environment has a compiler.
    /// </summary>
    [Fact]
    public void GetOrBuild_HonoursSoOverride_WhenFileExists()
    {
        var dir = Path.Combine(Path.GetTempPath(), "win32stubs-test-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var cFile = Path.Combine(dir, "trivial.c");
        var soFile = Path.Combine(dir, "trivial.so");
        File.WriteAllText(cFile, "int dummy_export(void) { return 42; }\n");

        var psi = new System.Diagnostics.ProcessStartInfo("cc", $"-shared -fPIC -o \"{soFile}\" \"{cFile}\"")
        { RedirectStandardError = true, UseShellExecute = false };
        using var proc = System.Diagnostics.Process.Start(psi)!;
        proc.WaitForExit(10000);
        // Skip (not fail) on a machine with no compiler at all — the override path itself
        // is what's under test, not whether this box can compile C.
        if (proc.ExitCode != 0) return;

        var saved = Environment.GetEnvironmentVariable("AL_RUNNER_WIN32_STUBS_SO");
        try
        {
            Environment.SetEnvironmentVariable("AL_RUNNER_WIN32_STUBS_SO", soFile);
            Win32Stubs.ResetForTests();
            var handle = Win32Stubs.GetOrBuild("kernel32.dll");
            Assert.NotEqual(IntPtr.Zero, handle);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AL_RUNNER_WIN32_STUBS_SO", saved);
            Win32Stubs.ResetForTests();
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// RED-shaped negative: an override pointing at a nonexistent path must fail loudly and
    /// name the bad path, not silently fall through to trying to build from source.
    /// </summary>
    [Fact]
    public void GetOrBuild_Throws_WhenSoOverridePointsAtMissingFile()
    {
        var saved = Environment.GetEnvironmentVariable("AL_RUNNER_WIN32_STUBS_SO");
        var missing = Path.Combine(Path.GetTempPath(), "win32stubs-does-not-exist-" + Guid.NewGuid() + ".so");
        try
        {
            Environment.SetEnvironmentVariable("AL_RUNNER_WIN32_STUBS_SO", missing);
            Win32Stubs.ResetForTests();
            var ex = Assert.Throws<InvalidOperationException>(
                () => Win32Stubs.GetOrBuild("kernel32.dll"));
            Assert.Contains(missing, ex.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AL_RUNNER_WIN32_STUBS_SO", saved);
            Win32Stubs.ResetForTests();
        }
    }
}
