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
    [SkippableFact]
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
        TestArtifacts.SkipIf(proc.ExitCode != 0,
            $"no working C compiler on this machine: `cc -shared` exited {proc.ExitCode}.");

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

    // ---------------------------------------------------------------------------------
    // #1672: shipped prebuilt libwin32_stubs.so — no C compiler required on Linux.
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// Pure lookup: the current RID's filename must be present when a fixture file with
    /// that exact name exists. Would a stub that always returns null (or always returns
    /// some hardcoded path regardless of the `exists` probe) pass this? No — this asserts
    /// the exact composed path, keyed off <see cref="Win32Stubs.PrebuiltStubFileName"/>,
    /// which is itself keyed off the live <c>RuntimeInformation.ProcessArchitecture</c> so
    /// the test can't fake a mismatched RID past it.
    /// </summary>
    [SkippableFact]
    public void LocatePrebuiltSo_ReturnsComposedPath_WhenFileExists()
    {
        var name = Win32Stubs.PrebuiltStubFileName();
        TestArtifacts.SkipIf(name is null,
            $"no prebuilt-stub convention for this RID ({System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier}) — Linux x64/arm64 only.");
        var expected = Path.Combine("/fake/base", "Win32Stubs", name);

        var found = Win32Stubs.LocatePrebuiltSo("/fake/base", path => path == expected);

        Assert.Equal(expected, found);
    }

    /// <summary>
    /// Negative: when the file genuinely isn't there, LocatePrebuiltSo must return null
    /// (not throw, not fall back to some default) — GetOrBuild relies on that null to know
    /// it should proceed to the compile-from-source path.
    /// </summary>
    [Fact]
    public void LocatePrebuiltSo_ReturnsNull_WhenFileMissing()
    {
        var found = Win32Stubs.LocatePrebuiltSo("/fake/base", _ => false);
        Assert.Null(found);
    }

    /// <summary>
    /// PrebuiltStubFileName must actually vary by architecture — pinning that x64 and
    /// arm64 produce different filenames catches a copy-paste bug that would silently load
    /// the wrong architecture's shim (an ELF class mismatch that fails at NativeLibrary.Load
    /// time with a confusing "wrong ELF class" error, not at compile time).
    /// </summary>
    [SkippableFact]
    public void PrebuiltStubFileName_NamesTheRidExplicitly_WhenSupported()
    {
        var name = Win32Stubs.PrebuiltStubFileName();
        TestArtifacts.SkipIf(name is null,
            $"no prebuilt-stub convention for this RID ({System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier}) — Linux x64/arm64 only.");
        Assert.True(name is "libwin32_stubs.linux-x64.so" or "libwin32_stubs.linux-arm64.so",
            $"Unexpected prebuilt stub filename: {name}");
    }

    /// <summary>
    /// GREEN, end-to-end: with a fixture .so dropped at the exact beside-the-binary path
    /// GetOrBuild probes (via the BaseDirectoryForTests test seam), GetOrBuild must load it
    /// directly — with zero compiler invocation. Proven here by additionally forcing the
    /// "no compiler reachable" branch via the PathEnvironmentForTests seam (#1809 — NOT a real
    /// PATH mutation; see that seam's doc comment for why): if GetOrBuild fell through to the
    /// compile-from-source path instead of using the prebuilt stub, it would throw (no
    /// compiler reachable) rather than return a valid handle.
    /// </summary>
    [SkippableFact]
    public void GetOrBuild_LoadsShippedPrebuiltStub_WithoutInvokingAnyCompiler()
    {
        var ridName = Win32Stubs.PrebuiltStubFileName();
        TestArtifacts.SkipIf(ridName is null,
            $"no prebuilt-stub convention for this RID ({System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier}) — Linux x64/arm64 only.");

        var dir = Path.Combine(Path.GetTempPath(), "win32stubs-prebuilt-test-" + Guid.NewGuid());
        var stubDir = Path.Combine(dir, "Win32Stubs");
        Directory.CreateDirectory(stubDir);
        var cFile = Path.Combine(dir, "trivial.c");
        var soFile = Path.Combine(stubDir, ridName);
        File.WriteAllText(cFile, "int dummy_export(void) { return 42; }\n");

        var psi = new System.Diagnostics.ProcessStartInfo("cc", $"-shared -fPIC -o \"{soFile}\" \"{cFile}\"")
        { RedirectStandardError = true, UseShellExecute = false };
        using var proc = System.Diagnostics.Process.Start(psi)!;
        proc.WaitForExit(10000);
        // Skip (not fail) on a machine with no compiler — building the FIXTURE needs cc,
        // but the behaviour under test (GetOrBuild not needing cc at RUN time) doesn't.
        if (proc.ExitCode != 0) { try { Directory.Delete(dir, true); } catch { } return; }

        var savedOverride = Environment.GetEnvironmentVariable("AL_RUNNER_WIN32_STUBS_SO");
        try
        {
            Environment.SetEnvironmentVariable("AL_RUNNER_WIN32_STUBS_SO", null);
            Win32Stubs.PathEnvironmentForTests = ""; // no compiler reachable — process-local seam, not real PATH
            Win32Stubs.BaseDirectoryForTests = dir;
            Win32Stubs.ResetForTests();

            var handle = Win32Stubs.GetOrBuild("kernel32.dll");
            Assert.NotEqual(IntPtr.Zero, handle);
        }
        finally
        {
            Win32Stubs.PathEnvironmentForTests = null;
            Environment.SetEnvironmentVariable("AL_RUNNER_WIN32_STUBS_SO", savedOverride);
            Win32Stubs.BaseDirectoryForTests = null;
            Win32Stubs.ResetForTests();
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Negative direction for the new load-order step: with no prebuilt stub AND no
    /// compiler reachable (via the PathEnvironmentForTests seam — #1809, not a real PATH
    /// mutation), GetOrBuild must still fail loudly with #1669's message — the new
    /// "check for a prebuilt" step must not itself swallow the absence and return a
    /// default/zero handle.
    /// </summary>
    [Fact]
    public void GetOrBuild_StillThrows_WhenNoPrebuiltAndNoCompiler()
    {
        var dir = Path.Combine(Path.GetTempPath(), "win32stubs-no-prebuilt-test-" + Guid.NewGuid());
        Directory.CreateDirectory(dir); // deliberately no Win32Stubs/ subfolder inside it

        var savedOverride = Environment.GetEnvironmentVariable("AL_RUNNER_WIN32_STUBS_SO");
        try
        {
            Environment.SetEnvironmentVariable("AL_RUNNER_WIN32_STUBS_SO", null);
            Win32Stubs.PathEnvironmentForTests = ""; // no compiler reachable — process-local seam, not real PATH
            Win32Stubs.BaseDirectoryForTests = dir;
            Win32Stubs.ResetForTests();

            var ex = Assert.Throws<InvalidOperationException>(() => Win32Stubs.GetOrBuild("kernel32.dll"));
            Assert.Contains("kernel32.dll", ex.Message);
        }
        finally
        {
            Win32Stubs.PathEnvironmentForTests = null;
            Environment.SetEnvironmentVariable("AL_RUNNER_WIN32_STUBS_SO", savedOverride);
            Win32Stubs.BaseDirectoryForTests = null;
            Win32Stubs.ResetForTests();
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Issue #1673 (regression from #1669): on Windows, kernel32/user32/etc. are the *real*
    /// Win32 libraries — the Linux-only shim resolver must never intercept them at all.
    /// Before #1669 an unconditional interception was harmless because the resolver's failure
    /// was swallowed and the default loader took over; after #1669 it throws directly, which
    /// on Windows means every AL install trigger that touches WindowsLanguageHelper (e.g. a
    /// bare TextConstant, see #1651) now dies with a Linux-shim error on a platform that never
    /// needed the shim. <see cref="Win32Stubs.Register(bool)"/> must skip registration entirely
    /// when told it's running on Windows, and — critically — must do so *before* setting the
    /// "already registered" flag, so a later non-Windows call in the same process still works.
    /// </summary>
    [Fact]
    public void Register_OnWindows_NeverMarksItselfRegistered()
    {
        Win32Stubs.ResetForTests();
        Win32Stubs.Register(isWindows: true);
        Assert.False(Win32Stubs.IsRegisteredForTests,
            "Register(isWindows: true) must no-op without setting the registered flag, " +
            "so the Win32 shim resolver is never installed for Nav.* assemblies on Windows.");
    }

    /// <summary>
    /// Positive counterpart: the non-Windows path must be unaffected by the platform guard —
    /// it still marks itself registered exactly as before #1673's fix. A guard that always
    /// no-ops (e.g. `return;` unconditionally) would make the negative test above pass too,
    /// so this positive case is required to prove the guard is actually platform-conditional.
    /// </summary>
    [Fact]
    public void Register_OnNonWindows_StillMarksItselfRegistered()
    {
        Win32Stubs.ResetForTests();
        Win32Stubs.Register(isWindows: false);
        Assert.True(Win32Stubs.IsRegisteredForTests,
            "Register(isWindows: false) must behave exactly as before #1673 and register normally.");
    }
}
