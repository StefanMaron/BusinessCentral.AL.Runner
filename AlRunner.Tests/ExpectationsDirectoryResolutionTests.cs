// ExpectationsDirectoryResolutionTests — pins the resolver at #1984's core: the
// auto-probed tests/expectations manifest must be found relative to the BUNDLE
// path, not only relative to cwd.
//
// Before the fix, Program.cs's auto-probe was a single
// `Directory.Exists(Path.Combine(Environment.CurrentDirectory, "tests", "expectations"))`
// check — so `al-runner ... tests/al-language/tests/al-language` found the manifest
// only when cwd happened to be the repo root, and missed it (silently) from every
// other cwd, even though the SAME bundle sits inside a checkout whose tests/expectations
// is perfectly discoverable by walking up from the bundle argument.
//
// These tests exercise ExpectationsDirectoryResolution.Resolve directly — no
// subprocess, no BC engine — so they are fast and give an unambiguous RED→GREEN
// signal for the resolution algorithm itself. ExpectationManifestWiringTests carries
// the slower end-to-end proof that Program.cs actually calls this.
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class ExpectationsDirectoryResolutionTests : IDisposable
{
    private readonly string _root;

    public ExpectationsDirectoryResolutionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "al-runner-expectations-resolve", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>
    /// The reported bug (#1984), reproduced without a subprocess: a manifest that
    /// lives several levels above the bundle path must be found by walking up from
    /// the bundle argument, even though cwd is a completely unrelated directory that
    /// has no tests/expectations anywhere above it.
    /// </summary>
    [Fact]
    public void Resolve_WalksUpFromBundlePath_EvenWhenCwdHasNoManifest()
    {
        var repoLike = Path.Combine(_root, "fake-repo");
        var manifestDir = Path.Combine(repoLike, "tests", "expectations");
        Directory.CreateDirectory(manifestDir);
        var bundleDir = Path.Combine(repoLike, "tests", "al-language", "tests", "al-language");
        Directory.CreateDirectory(bundleDir);

        var unrelatedCwd = Path.Combine(_root, "unrelated-cwd");
        Directory.CreateDirectory(unrelatedCwd);

        var resolved = ExpectationsDirectoryResolution.Resolve(new[] { bundleDir }, unrelatedCwd);

        Assert.Equal(manifestDir, resolved);
    }

    /// <summary>
    /// Negative: when NEITHER the bundle path's ancestors NOR cwd have a
    /// tests/expectations directory, resolution must return null (not throw, not
    /// guess) — the caller is responsible for making that loud.
    /// </summary>
    [Fact]
    public void Resolve_NoManifestAnywhere_ReturnsNull()
    {
        var bundleDir = Path.Combine(_root, "isolated", "bundle");
        Directory.CreateDirectory(bundleDir);
        var unrelatedCwd = Path.Combine(_root, "isolated", "cwd");
        Directory.CreateDirectory(unrelatedCwd);

        var resolved = ExpectationsDirectoryResolution.Resolve(new[] { bundleDir }, unrelatedCwd);

        Assert.Null(resolved);
    }

    /// <summary>
    /// Back-compat: the original cwd-only behaviour still works when the bundle path
    /// itself has no reachable manifest but cwd does — a relative bundle path
    /// invoked from the repo root (the pre-#1984 working case) must keep working.
    /// </summary>
    [Fact]
    public void Resolve_FallsBackToCwd_WhenBundlePathHasNoManifest()
    {
        var manifestDir = Path.Combine(_root, "tests", "expectations");
        Directory.CreateDirectory(manifestDir);
        var bundleOutsideRoot = Path.Combine(Path.GetTempPath(), "al-runner-expectations-resolve-elsewhere", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(bundleOutsideRoot);
        try
        {
            var resolved = ExpectationsDirectoryResolution.Resolve(new[] { bundleOutsideRoot }, _root);
            Assert.Equal(manifestDir, resolved);
        }
        finally
        {
            try { Directory.Delete(bundleOutsideRoot, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// A nonexistent bundle path (e.g. mistyped, or diagnosed elsewhere by
    /// BundleRootValidation) must not throw — it degrades to walking up from its
    /// would-be parent directory rather than crashing the auto-probe.
    /// </summary>
    [Fact]
    public void Resolve_NonexistentBundlePath_DoesNotThrow_WalksUpFromParent()
    {
        var manifestDir = Path.Combine(_root, "tests", "expectations");
        Directory.CreateDirectory(manifestDir);
        var nonexistentBundle = Path.Combine(_root, "does-not-exist");
        var unrelatedCwd = Path.Combine(_root, "unrelated-cwd2");
        Directory.CreateDirectory(unrelatedCwd);

        var resolved = ExpectationsDirectoryResolution.Resolve(new[] { nonexistentBundle }, unrelatedCwd);

        Assert.Equal(manifestDir, resolved);
    }
}
