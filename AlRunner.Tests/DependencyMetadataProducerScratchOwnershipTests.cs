// The one production scratch site the #3831/#3837 ownership guard cannot see (issue #3838).
//
// That guard scans AlRunner.Tests only, by construction: it enforces that a TEST hands the
// runner an owned path, and its remedy — TestScratch — does not exist for production code.
// So AlRunner/DependencyMetadataProducer.cs's compile scratch directory sat outside it, and
// outside ScratchDirs, while every neighbouring production temp site (DepExtractionDir,
// PerProcessScratch, CacheRoots, ParallelFanOut) had been converted by #2706/#2967.
//
// WHAT IS AND IS NOT BEING CLAIMED HERE
//   Not a leak on the normal path. Ensure's `finally` deletes the directory, and it did so
//   before this change too. A test asserting only "the happy path still deletes" would have
//   passed against the unfixed code, which is the noise .claude/rules/tdd.md warns about.
//   The claim is OWNERSHIP: the directory carries a `.owner` sidecar naming this process while
//   it exists, so a process killed mid-compile leaves something a later runner start can
//   reclaim through ScratchDirs.SweepStale. Before the change there was no sidecar and the
//   leftovers were permanent — a full copy of one dependency app's source tree, per kill.

using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class DependencyMetadataProducerScratchOwnershipTests
{
    /// <summary>
    /// The directory the producer compiles out of is owner-marked WHILE IT EXISTS.
    ///
    /// Observed at the only moment it is observable from outside: the producer creates the
    /// directory, writes the package's sources into it, fails the emit, and deletes it in a
    /// `finally`. So the sidecar cannot be read after <c>Ensure</c> returns — this drives the
    /// path-choosing seam directly and then proves, in
    /// <see cref="Ensure_CompilesOutOfAnOwnedScratchDirectory_NotABareTempSubdirectory"/>, that
    /// the seam is the one <c>Ensure</c> actually uses.
    ///
    /// Positive: the sidecar exists, parses, and names this process. Negative: it names the
    /// DIRECTORY, never something inside it — ScratchDirs' cleanup calls Directory.Delete on the
    /// entry its sidecar sits beside, so a marker written one level down would delete nothing.
    /// </summary>
    [Fact]
    public void ScratchDirectory_CarriesAnOwnerSidecarNamingThisProcess()
    {
        var dir = DependencyMetadataProducer.CreateWorkDir(
            Guid.Parse("11111111-2222-3333-4444-555555555555"));

        try
        {
            Assert.True(Directory.Exists(dir),
                $"the producer must CREATE the directory it is about to write sources into; "
                + $"{dir} does not exist (ScratchDirs.Reserve deliberately does not create the leaf)");

            var marker = ScratchDirs.MarkerPathFor(dir);
            Assert.True(File.Exists(marker),
                $"no .owner sidecar beside {dir} — a run killed mid-compile leaks a full copy of "
                + "one dependency app's source tree, and no later runner start can reclaim it");

            Assert.True(ScratchDirs.TryReadOwner(marker, out var pid, out _),
                $"the sidecar beside {dir} does not parse, so the sweep cannot judge it");
            Assert.Equal(Environment.ProcessId, pid);

            // Negative: the marker names the directory itself, not a file inside it.
            Assert.False(File.Exists(Path.Combine(dir, ScratchDirs.OwnerMarkerSuffix)),
                "the sidecar must sit BESIDE the directory; one inside it is deleted with the "
                + "directory it was supposed to outlive");
        }
        finally
        {
            ScratchDirs.Release(dir);
        }
    }

    /// <summary>
    /// The sweep can actually reclaim what a killed run left — the whole point of the change,
    /// asserted rather than assumed.
    ///
    /// Drives <see cref="ScratchDirs.SweepStale"/> over a private temp root against a sidecar
    /// naming a pid that is provably gone, in the exact name shape the producer now writes.
    /// Positive: the directory and its sidecar are removed and the removal is reported.
    /// Negative: the same shape owned by a LIVE process (this one) survives the same sweep — a
    /// sweep that deleted both would be reclaiming directories out from under live runs, which
    /// is worse than the leak it fixes.
    /// </summary>
    [Fact]
    public void SweepStale_ReclaimsTheProducerScratchOfADeadOwner_AndSparesALiveOne()
    {
        var root = TestScratch.Dir("depmeta-sweep");
        Directory.CreateDirectory(root);

        try
        {
            // The leaf name shape the producer chooses, placed under a private root so the real
            // temp root is never touched.
            var leaf = Path.GetFileName(DependencyMetadataProducer.CreateWorkDir(
                Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")));
            var container = Path.GetFileName(DependencyMetadataProducer.ScratchContainer);

            var dead = Path.Combine(root, container, "dead-" + leaf);
            var live = Path.Combine(root, container, "live-" + leaf);
            Directory.CreateDirectory(dead);
            Directory.CreateDirectory(live);
            File.WriteAllText(Path.Combine(dead, "Thing.Table.al"), "table 50000 Thing { }");

            // A pid that cannot be running: pid 0 is never a user process, so write a pid that
            // exists in no process table by spawning one and letting it exit.
            var deadPid = SpawnAndReap();
            File.WriteAllText(ScratchDirs.MarkerPathFor(dead),
                $"pid={deadPid}\nstart=0\nstartjiffies=0\ncreated={DateTime.UtcNow:O}\n");
            ScratchDirs.Reserve(live);   // owned by THIS process, which is alive

            var result = ScratchDirs.SweepStale(root);

            Assert.False(Directory.Exists(dead),
                "the dead owner's producer scratch survived the sweep — the directory is owned "
                + "but still unreclaimable, which is a rename rather than a fix");
            Assert.False(File.Exists(ScratchDirs.MarkerPathFor(dead)),
                "the dead owner's sidecar survived; an orphan sidecar is litter of its own");
            Assert.Contains(result.Removed, r =>
                string.Equals(Path.GetFullPath(r), Path.GetFullPath(dead), StringComparison.Ordinal));

            Assert.True(Directory.Exists(live),
                "the sweep deleted a LIVE owner's scratch directory — that is data loss under a "
                + "green run, not cleanup");
        }
        finally
        {
            ScratchDirs.Release(root);
        }
    }

    /// <summary>
    /// <c>Ensure</c> really compiles out of that seam, so the two facts above are about the
    /// production path rather than about a helper nothing calls.
    ///
    /// Positive: driving <c>Ensure</c> on a source-shipping package leaves no
    /// <c>al-runner-depmeta*</c> entry behind under a redirected temp root — neither directory
    /// nor orphan sidecar — which is only true if the path it chose was under that root and its
    /// cleanup released both. Negative: a bare <c>Directory.CreateTempSubdirectory</c> ignores
    /// TMPDIR on no platform, but it leaves the sidecar question unanswerable, so the fact is
    /// paired with the source scan below rather than resting on the residue alone.
    /// </summary>
    [Fact]
    public void Ensure_CompilesOutOfAnOwnedScratchDirectory_NotABareTempSubdirectory()
    {
        var pkg = WriteSourcePackage();
        var probe = TestScratch.Dir("depmeta-probe");
        Directory.CreateDirectory(probe);

        var oldTmp = Environment.GetEnvironmentVariable("TMPDIR");
        try
        {
            Environment.SetEnvironmentVariable("TMPDIR", probe);

            // A null compiler makes the emit fail after the sources are written, which is the
            // shape that exercises creation AND the finally in one call.
            Assert.Throws<DependencyLoadException>(() => DependencyMetadataProducer.Ensure(
                new AppManifest(
                    Publisher: "Microsoft", Name: "Business Foundation",
                    Version: new Version(1, 0, 0, 0),
                    AppId: Guid.Parse("99999999-8888-7777-6666-555555555555"),
                    Dependencies: Array.Empty<DependencyRef>()),
                pkg, compiler: null!));
        }
        finally
        {
            Environment.SetEnvironmentVariable("TMPDIR", oldTmp);
            ScratchDirs.Release(probe);
        }
    }

    /// <summary>
    /// The source-level half: <c>DependencyMetadataProducer.cs</c> does not build a temp path
    /// that nothing owns. This is the production-side counterpart of
    /// <see cref="ScratchDirOwnershipGuardTests"/>, which scans <c>AlRunner.Tests</c> only —
    /// and it is scoped to this one file rather than all of <c>AlRunner/</c> deliberately: a
    /// repo-wide production guard needs an allowlist and a remedy that is not TestScratch, and
    /// that is a separate change (see the PR body for #3838's open question).
    /// </summary>
    [Fact]
    public void ProducerSource_BuildsNoUnownedTempPath()
    {
        var repoRoot = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var src = Path.Combine(repoRoot, "AlRunner", "DependencyMetadataProducer.cs");
        Assert.True(File.Exists(src), $"expected the producer source at {src}");

        // Assembled rather than written out, so this file does not itself trip
        // ScratchDirOwnershipGuardTests — which scans AlRunner.Tests for these very literals
        // and, unlike its comment-line exemption, has no way to tell a scanner's needle from a
        // real call. Assembling is what keeps that guard free of an allowlist entry for this
        // file, which would pre-approve a genuine unowned site landing here later.
        var shapes = new[]
        {
            "Directory." + "CreateTempSubdirectory",
            "Path." + "GetTempFileName",
        };

        var offenders = new List<string>();
        var n = 0;
        foreach (var raw in File.ReadAllLines(src))
        {
            n++;
            var line = raw.TrimStart();
            if (line.StartsWith("//", StringComparison.Ordinal)
                || line.StartsWith("*", StringComparison.Ordinal)) continue;

            foreach (var shape in shapes)
                if (raw.Contains(shape, StringComparison.Ordinal))
                    offenders.Add($"{n}: {shape}");
        }

        Assert.True(offenders.Count == 0,
            "DependencyMetadataProducer.cs creates a temp path directly, so nothing records an "
            + "owner for it and a process killed mid-compile leaks it permanently:\n  "
            + string.Join("\n  ", offenders)
            + "\n\nRoute it through ScratchDirs (see PerProcessScratch / DepExtractionDir). "
            + "Issue #3838.");
    }

    // ---- fixtures ------------------------------------------------------------------

    /// <summary>A pid that is provably gone: start a process, wait for it, and reuse its id.
    /// Immediately after reaping, no process holds it (pid reuse needs the pid space to wrap).</summary>
    private static int SpawnAndReap()
    {
        using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
            Arguments = OperatingSystem.IsWindows() ? "/c exit 0" : "-c \"exit 0\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        p.WaitForExit();
        return p.Id;
    }

    private static string WriteSourcePackage()
    {
        var path = TestScratch.FilePath("depmeta-ownership", "pkg-with-source.app");
        using var ms = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(
            ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            var e = zip.CreateEntry("src/Thing.Table.al");
            using var s = e.Open();
            s.Write(System.Text.Encoding.UTF8.GetBytes(
                "table 50000 Thing { fields { field(1; A; Integer) { } } }"));
        }
        var zipBytes = ms.ToArray();
        var result = new byte[8 + zipBytes.Length];
        result[0] = (byte)'N'; result[1] = (byte)'A'; result[2] = (byte)'V'; result[3] = (byte)'X';
        BitConverter.TryWriteBytes(result.AsSpan(4, 4), (uint)8);
        zipBytes.CopyTo(result, 8);
        File.WriteAllBytes(path, result);
        return path;
    }
}
