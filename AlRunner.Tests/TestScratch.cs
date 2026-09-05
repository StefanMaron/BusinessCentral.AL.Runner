// TestScratch — per-run scratch directories for tests, owned and reclaimable (#2706).
//
// Tests in this project hand the runner fresh GUID-named directories under Path.GetTempPath()
// as bundles and — the expensive case — as `--cache` roots, into which the runner then builds a
// complete BC cache (~273 MB: r2r-chunks, bc-symbols, the Cecil-rewritten Ncl, ...). Written as
// `Path.Combine(Path.GetTempPath(), "<name>", Guid.NewGuid().ToString("N"))` and never deleted,
// those had accumulated 126 GB on one machine; and on a stock Linux desktop /tmp is a tmpfs,
// so a contributor without a TMPDIR redirect pays that in RAM.
//
// Both helpers return exactly the path shape the hand-written expression produced, so a call
// site's semantics are unchanged (the leaf is NOT created here — some tests rely on observing
// whether the runner created it). What changes is ownership: ScratchDirs writes a `.owner`
// sidecar naming this test host, deletes every reserved directory at the host's ProcessExit,
// and the next runner start reclaims whatever a KILLED host left behind. A test that also
// deletes its directory itself keeps doing so; the sidecar is a second net, not a replacement.

using AlRunner.Infrastructure;

namespace AlRunner.Tests;

internal static class TestScratch
{
    /// <summary><c>&lt;temp&gt;/&lt;prefix&gt;/&lt;guid&gt;</c> — the nested shape, one container per fixture kind.</summary>
    public static string Dir(string prefix)
        => ScratchDirs.Reserve(Path.Combine(Path.GetTempPath(), prefix, Guid.NewGuid().ToString("N")));

    /// <summary><c>&lt;temp&gt;/&lt;prefix&gt;&lt;guid&gt;</c> — the flat shape (prefix usually ends in '-').</summary>
    public static string FlatDir(string prefix)
        => ScratchDirs.Reserve(Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N")));
}
