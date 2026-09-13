using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Cross-PROCESS persistence of the #1867 dependency+company install baseline.
///
/// #1867 stopped the dependency Install triggers + Company-Initialize (codeunit 2) from
/// being re-run for every app group inside ONE process. What it could not remove is the
/// per-process cost: the in-memory dictionary dies with the process, so every new
/// `al-runner` invocation recomputed the whole thing — measured at 5.9s of a 23.3s warm
/// single-fixture run (96.1% of that app group's run_ms). The result is a pure function of
/// (dependency assembly set, runner build, BC version), so it is now serialised to
/// <c>&lt;cache-root&gt;/install-baseline/&lt;sha256&gt;.bin</c> and reloaded by the next
/// process (see AlRunner/Infrastructure/InstallBaselineDiskCache.cs and
/// AlRunner/Patches/RecordPatches.InstallBaselineDisk.cs).
///
/// A cache that merely runs faster is not the claim under test. The claims are:
///
///   1. ROUND TRIP — what the second process restores from disk is the SAME state the first
///      process captured, value for value. Asserted on the <c>digest=</c> the two processes
///      log: a SHA-256 over every persisted table's every row's every field slot, carrying
///      that value's own NclType, its own defined length, its NULL flag and the exact bytes
///      BC's <c>NavValue.GetBytes()</c> produces, plus the isolated-storage and
///      auto-increment state. Record links are in there as ordinary table rows: the seed's
///      install trigger attaches one, and the Record Link table (2000000068) is persisted
///      like any other (#3380). Two independent fresh computations do NOT produce the same
///      digest (BC assigns a new SystemId GUID and SystemCreatedAt on every Insert), so an
///      equal digest across two processes is only obtainable by genuinely reloading the
///      first one's values — it cannot be faked by recomputing.
///   2. KILL SWITCH — AL_RUNNER_NO_DEP_COMPANY_CACHE=1 disables the disk tier in BOTH
///      directions: no read (a present entry is not consulted) and no write (the entry on
///      disk is left byte-identical).
///   3. CORRUPTION — a damaged entry is detected, deleted, recomputed and rewritten, and the
///      rewritten entry is itself usable by the run after it.
///   4. SCOPING — two app groups whose dependency closures differ get two different keys and
///      therefore two different files; a baseline is never shared across closures.
///
/// Every case also asserts the app group's own AL test still passes, reading the rows the
/// dependency's OnInstallAppPerCompany trigger seeded back BY VALUE — so a "cache" that
/// restored nothing at all fails the assertion rather than merely running fast.
///
/// #2364: these fixtures used to declare the Base Application floor and assert on Company
/// Information's Company-Initialize-seeded singleton row. Neither this suite's claims nor
/// #1867's are about Base Application — what they need is a dependency closure whose install
/// triggers WRITE ROWS, because an empty snapshot is never persisted
/// (<c>not persisting: snapshot has 0 DataAccessSource(s)</c>) and leaves every assertion
/// here with nothing to observe. <see cref="InstallSeedClosure"/> supplies exactly that
/// closure and nothing else; see .claude/rules/no-base-app-in-csharp-tests.md for what the
/// floor cost, and that file's header for the measurement of dropping it.
///
/// Spawns the real runner; needs the BC artifact cache. Skips (no-op) when absent.
/// </summary>
public class InstallBaselineDiskCacheTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private static (string output, int exit) RunRunner(
        IDictionary<string, string>? extraEnv, params string[] bundles)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        args.Append(" --package-cache \"").Append(TestArtifacts.PlatformAppsDir()).Append('"');
        foreach (var b in bundles) args.Append(" \"").Append(b).Append('"');
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet", Arguments = args.ToString(),
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
            // PERF gives the DepCompanyCache markers (and the digest); VERBOSE lets the
            // [InstallBaselineDisk] component lines through Log.cs's tag filter, which is how
            // these tests learn which file on disk the run used.
            Environment = { ["AL_RUNNER_PERF"] = "1", ["AL_RUNNER_VERBOSE"] = "1" },
        };
        if (extraEnv != null)
            foreach (var (k, v) in extraEnv) psi.Environment[k] = v;
        var sb = new StringBuilder();
        var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(300_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        lock (sb) return (sb.ToString(), p.ExitCode);
    }

    /// <summary>
    /// A dependency closure that seeds rows and is unique to this test invocation. See
    /// <see cref="InstallSeedClosure"/> for why the seed app is a sibling source dependency
    /// rather than a second bundle, and for how the fresh-GUID identity guarantees the
    /// on-disk entry starts out absent — without which these tests could not tell a genuine
    /// first-run MISS from a hit on an entry some earlier run left behind.
    /// </summary>
    /// <returns>The one directory to hand the runner.</returns>
    private static string WriteUniqueClosure(string root, int baseId, string tag)
        => InstallSeedClosure.Write(root, tag, baseId).BundleDir;

    // ── log parsing ────────────────────────────────────────────────────────────────────

    private static readonly Regex WriteLine = new(
        @"InstallBaseline\.DepCompanyCache DISK-WRITE (\S+) (\d+)B digest=(\S+)", RegexOptions.Compiled);
    private static readonly Regex HitLine = new(
        @"InstallBaseline\.DepCompanyCache DISK-HIT (\S+) digest=(\S+)", RegexOptions.Compiled);
    private static readonly Regex WrotePathLine = new(
        @"\[InstallBaselineDisk\] wrote \d+ byte\(s\) to (.+)$", RegexOptions.Compiled | RegexOptions.Multiline);

    private static Dictionary<string, string> WriteDigests(string output) =>
        WriteLine.Matches(output).ToDictionary(m => m.Groups[1].Value, m => m.Groups[3].Value);

    private static Dictionary<string, string> HitDigests(string output) =>
        HitLine.Matches(output).ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);

    private static List<string> WrittenPaths(string output) =>
        WrotePathLine.Matches(output).Select(m => m.Groups[1].Value.Trim()).Distinct().ToList();

    private static int Count(string haystack, string needle)
    {
        int count = 0, idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += needle.Length;
        }
        return count;
    }

    // ── 1. round trip across processes ─────────────────────────────────────────────────

    [SkippableFact]
    public void SecondProcess_RestoresByteEquivalentBaselineFromDisk()
    {
        TestArtifacts.SkipIfMissing();

        var root = TestScratch.Dir("al-runner-ib-disk-roundtrip");
        try
        {
            var main = WriteUniqueClosure(root, 62000, "rt");

            var (out1, exit1) = RunRunner(null, main);
            Assert.Equal(0, exit1);
            Assert.True(Count(out1, "1P/0F/0E") >= 1, $"main app group should pass, got:\n{out1}");

            // [THEN] First process: this closure has never been seen, so its key is computed
            // fresh and persisted, and NOTHING in this process is restored from disk. #2364
            // made that the flat assertion it now is: while the seed app was passed as its own
            // bundle it formed a second app group whose dependency closure was empty, and an
            // empty snapshot is never persisted — so that group logged a MISS on every process
            // forever and this could only be written as a key-set intersection.
            var written = WriteDigests(out1);
            Assert.Single(written);
            Assert.Empty(HitDigests(out1));
            Assert.Equal(1, Count(out1, "InstallBaseline.DepCompanyCache MISS"));

            var (out2, exit2) = RunRunner(null, main);
            Assert.Equal(0, exit2);
            Assert.True(Count(out2, "1P/0F/0E") >= 1, $"main app group should pass, got:\n{out2}");

            // [THEN] Second process: no fresh computation for ANY key in the bundle, and
            // nothing rewritten.
            Assert.Equal(0, Count(out2, "InstallBaseline.DepCompanyCache MISS"));
            Assert.Empty(WriteDigests(out2));

            // [THEN] Every key the first process wrote came back in the second, with the
            // IDENTICAL value-level digest — same tables, same rows, same field slots, same
            // NclTypes, same defined lengths, same NULL flags, same NavValue.GetBytes(). A
            // recomputation could not produce this: BC stamps a new SystemId GUID and
            // SystemCreatedAt on every Insert, so two fresh captures always differ.
            var hits = HitDigests(out2);
            foreach (var (key, digest) in written)
            {
                Assert.True(hits.ContainsKey(key), $"key {key} was written but not restored:\n{out2}");
                Assert.Equal(digest, hits[key]);
            }
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    // ── 2. kill switch: no read AND no write ───────────────────────────────────────────

    [SkippableFact]
    public void KillSwitch_SkipsBothTheDiskReadAndTheDiskWrite()
    {
        TestArtifacts.SkipIfMissing();

        var root = TestScratch.Dir("al-runner-ib-disk-killswitch");
        try
        {
            var main = WriteUniqueClosure(root, 62020, "ks");

            // Seed an entry that the kill-switched run would hit if the switch did nothing.
            var (out1, exit1) = RunRunner(null, main);
            Assert.Equal(0, exit1);
            var paths = WrittenPaths(out1);
            Assert.True(paths.Count >= 1, $"expected the first run to write an entry, got:\n{out1}");
            var before = paths.ToDictionary(p => p, File.ReadAllBytes);

            var (out2, exit2) = RunRunner(
                new Dictionary<string, string> { ["AL_RUNNER_NO_DEP_COMPANY_CACHE"] = "1" }, main);

            // [THEN] Seeding still happened — the switch disables the cache, not the work.
            Assert.Equal(0, exit2);
            Assert.True(Count(out2, "1P/0F/0E") >= 1, $"main app group should still pass, got:\n{out2}");

            // [THEN] Read side off: the entry that exists was not consulted (no DISK-HIT, and
            // no lookup was even attempted — the path line is emitted on every lookup).
            Assert.Empty(HitDigests(out2));
            Assert.DoesNotContain("[InstallBaselineDisk] entry path:", out2);
            Assert.True(Count(out2, "InstallBaseline.DepCompanyCache MISS") >= 1,
                $"expected fresh computation under the kill switch, got:\n{out2}");

            // [THEN] Write side off: the file on disk is byte-for-byte what the previous run
            // left. A switch that only skipped the read would have overwritten it here.
            Assert.Empty(WriteDigests(out2));
            foreach (var (path, bytes) in before)
                Assert.Equal(bytes, File.ReadAllBytes(path));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    // ── 3. corrupt entry: detected, replaced, and the replacement works ────────────────

    [SkippableFact]
    public void CorruptEntry_IsRejectedRecomputedAndRewrittenUsable()
    {
        TestArtifacts.SkipIfMissing();

        var root = TestScratch.Dir("al-runner-ib-disk-corrupt");
        try
        {
            var main = WriteUniqueClosure(root, 62040, "cx");

            var (out1, exit1) = RunRunner(null, main);
            Assert.Equal(0, exit1);
            var paths = WrittenPaths(out1);
            Assert.True(paths.Count >= 1, $"expected the first run to write an entry, got:\n{out1}");

            // Damage every entry this closure wrote: right magic, wrong everything after it,
            // so the run has to get past File.Exists and fail inside the decoder.
            foreach (var path in paths)
            {
                var junk = new byte[512];
                junk[0] = (byte)'A'; junk[1] = (byte)'L'; junk[2] = (byte)'I'; junk[3] = (byte)'B';
                for (var i = 4; i < junk.Length; i++) junk[i] = 0x5A;
                File.WriteAllBytes(path, junk);
            }

            var (out2, exit2) = RunRunner(null, main);

            // [THEN] The damage was noticed, not swallowed and not fatal.
            Assert.Equal(0, exit2);
            Assert.True(Count(out2, "1P/0F/0E") >= 1, $"main app group should still pass, got:\n{out2}");
            Assert.Contains("[InstallBaselineDisk] cannot restore:", out2);
            Assert.True(Count(out2, "InstallBaseline.DepCompanyCache MISS") >= 1,
                $"expected a fresh computation after the corrupt entry, got:\n{out2}");

            // [THEN] It was replaced, not merely skipped — every damaged file is now longer
            // than the 512-byte junk and no longer reads as junk.
            var rewritten = WriteDigests(out2);
            Assert.True(rewritten.Count >= 1, $"expected the corrupt entry to be rewritten, got:\n{out2}");
            foreach (var path in paths)
                Assert.True(new FileInfo(path).Length > 512, $"{path} was not rewritten");

            // [THEN] And the replacement is genuinely usable: a third process restores from it
            // with the digest the rewriting process recorded.
            var (out3, exit3) = RunRunner(null, main);
            Assert.Equal(0, exit3);
            Assert.Equal(0, Count(out3, "InstallBaseline.DepCompanyCache MISS"));
            var hits = HitDigests(out3);
            foreach (var (key, digest) in rewritten)
            {
                Assert.True(hits.ContainsKey(key), $"rewritten key {key} was not restored:\n{out3}");
                Assert.Equal(digest, hits[key]);
            }
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    // ── 3b. an entry written by the PREVIOUS build's codec ────────────────────────────

    /// <summary>
    /// #3380 — the on-disk layout changed (the record-link section is gone; link rows now ride
    /// in the Record Link table like any other table), so a file the previous build wrote must
    /// be REJECTED, not decoded under the new semantics.
    ///
    /// <para>Two independent mechanisms stand between those bytes and this run, and the test
    /// exists because only one of them is testable end to end. The first is the cache KEY:
    /// <c>InstallBaselineDiskCache.BuildKeyText</c> folds in both the schema version and
    /// <c>RunnerFingerprint</c>'s SHA-256 of the runner assembly, and the filename is that key's
    /// hash — so a previous build's file sits under a different name and is never opened. That
    /// is unreachable by construction and cannot be asserted by running the runner. The second
    /// is the in-file version check, which is what a filename collision or a hand-copied file
    /// would meet, and is what this plants.</para>
    ///
    /// <para>Schema version 2 is hardcoded on purpose: it is the historical format that carried
    /// the record-link section, not a value that tracks the current one.</para>
    /// </summary>
    [SkippableFact]
    public void EntryFromThePreviousSchemaVersion_IsRejectedByVersionAndRebuilt()
    {
        TestArtifacts.SkipIfMissing();

        var root = TestScratch.Dir("al-runner-ib-disk-oldformat");
        try
        {
            var main = WriteUniqueClosure(root, 62100, "of");

            var (out1, exit1) = RunRunner(null, main);
            Assert.Equal(0, exit1);
            var paths = WrittenPaths(out1);
            Assert.True(paths.Count >= 1, $"expected the first run to write an entry, got:\n{out1}");

            // Plant, at the key this closure resolves to, a file whose header says schema
            // version 2 — the format that carried RecordLinkPatches' section. The magic is
            // right, so File.Exists and the magic gate both pass and the version gate is the
            // thing under test.
            foreach (var path in paths)
            {
                var planted = new byte[512];
                planted[0] = (byte)'A'; planted[1] = (byte)'L'; planted[2] = (byte)'I'; planted[3] = (byte)'B';
                BitConverter.GetBytes(PreviousSchemaVersion).CopyTo(planted, 4);
                for (var i = 8; i < planted.Length; i++) planted[i] = 0x5A;
                File.WriteAllBytes(path, planted);
            }

            var (out2, exit2) = RunRunner(null, main);

            // [THEN] Rejected BY VERSION, naming both the file's and this build's — not
            // decoded and not diagnosed as some downstream shape error. That distinction is
            // the whole claim: an old file that still parses under new semantics is the one
            // failure a cache cannot detect for itself.
            Assert.Contains(
                $"[InstallBaselineDisk] cannot restore: schema version {PreviousSchemaVersion}, this build writes {CurrentSchemaVersion}",
                out2);
            Assert.DoesNotContain("[InstallBaselineDisk] cannot restore: EndOfStreamException", out2);

            // [THEN] Rebuilt, not fatal, and the app group's own AL test — which reads the
            // seeded rows AND the seeded record link back by value — still passes.
            Assert.Equal(0, exit2);
            Assert.True(Count(out2, "1P/0F/0E") >= 1, $"main app group should still pass, got:\n{out2}");
            Assert.True(Count(out2, "InstallBaseline.DepCompanyCache MISS") >= 1,
                $"expected a fresh computation after the old-format entry, got:\n{out2}");

            var rewritten = WriteDigests(out2);
            Assert.True(rewritten.Count >= 1, $"expected the old-format entry to be rewritten, got:\n{out2}");

            // [THEN] And the replacement is usable: a third process restores from it.
            var (out3, exit3) = RunRunner(null, main);
            Assert.Equal(0, exit3);
            Assert.Equal(0, Count(out3, "InstallBaseline.DepCompanyCache MISS"));
            var hits = HitDigests(out3);
            foreach (var (key, digest) in rewritten)
            {
                Assert.True(hits.ContainsKey(key), $"rewritten key {key} was not restored:\n{out3}");
                Assert.Equal(digest, hits[key]);
            }
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    /// <summary>The format that carried the record-link section — a historical constant, not a
    /// tracker of the current version.</summary>
    private const int PreviousSchemaVersion = 2;

    /// <summary>Read off the production constant rather than restated here, so a future bump
    /// does not need this file edited and cannot leave it asserting a stale number.</summary>
    private static int CurrentSchemaVersion =>
        (int)typeof(AlRunner.Patches.RecordPatches)
            .GetField("InstallBaselineDiskSchemaVersion",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.Static)!
            .GetRawConstantValue()!;

    // ── 4. scoping: a different dependency closure gets a different file ───────────────

    [SkippableFact]
    public void DifferentDependencyClosures_GetDifferentKeysAndDifferentFiles()
    {
        TestArtifacts.SkipIfMissing();

        var root = TestScratch.Dir("al-runner-ib-disk-scope");
        try
        {
            var mainA = WriteUniqueClosure(root, 62060, "sa");
            var mainB = WriteUniqueClosure(root, 62080, "sb");

            var (output, exit) = RunRunner(null, mainA, mainB);

            Assert.Equal(0, exit);
            Assert.True(Count(output, "1P/0F/0E") >= 2,
                $"expected both main app groups to pass, got:\n{output}");

            // [THEN] Two closures that differ by one dependency assembly produced two distinct
            // cache keys and two distinct files. A key that collapsed these two closures
            // together (or a path that collided) would show one.
            //
            // Measured by mutation (#2364): flattening the WHOLE
            // TestExecutor.CurrentInstallBaselineCacheKey() to a constant fails this test.
            // Flattening only its dependency-set component, or only its symbol-state
            // component, does NOT — the two are redundant here (#3254), so this pins the key as a
            // whole rather than the dependency set specifically. See the longer note in
            // InstallSeedDepCompanyCacheTests.AppGroupWithOwnDependencyApp_*.
            var written = WriteDigests(output);
            Assert.True(written.Count >= 2,
                $"expected at least 2 distinct dependency-closure keys to be persisted, got "
                + $"{written.Count}:\n{output}");
            Assert.True(WrittenPaths(output).Count >= 2,
                $"expected at least 2 distinct on-disk entries, got:\n{output}");

            // [THEN] …and neither closure reused the other's baseline.
            Assert.Empty(HitDigests(output).Keys.Intersect(written.Keys));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    // ── 5. the dependency-set term on its own (#3254) ──────────────────────────────────

    /// <summary>
    /// #3254: pins <c>InstallTriggerRunner.CurrentDependencySetKey()</c> specifically, which
    /// the scoping tests above cannot. Bundle order is the whole trick: the bundle WITH the extra
    /// dependency runs first, so by the time the seed-only bundle runs, the process-global
    /// registered .app set already holds both apps and
    /// <c>RecordPatches.RegisteredBcAppSymbolStateKey()</c> is the same for both app groups.
    /// Only the dependency-set term tells them apart.
    ///
    /// <para>The extra app's install trigger adds a third row to the seed table, so the two
    /// baselines differ by value: a key that collapsed them hands the seed-only group a
    /// three-row baseline, and its AL test fails on the row count.</para>
    ///
    /// <para>The second process, on the same cache root, checks the other direction: identical
    /// inputs restore both entries from disk with no recomputation.</para>
    /// </summary>
    [SkippableFact]
    public void DependencySetTermAlone_SeparatesBaselines_AndIdenticalInputsHitAcrossProcesses()
    {
        TestArtifacts.SkipIfMissing();

        var root = TestScratch.Dir("al-runner-ib-disk-depset");
        try
        {
            var (seedOnly, _, _) = InstallSeedClosure.WriteSharedClosure(root, "ds", 62120);
            var withExtra = InstallSeedClosure.WriteBundleWithSeedingExtraDependency(root, "dx", 62140, "ds");

            // ── process 1: the dependency set shrinks between the two app groups ──
            var (out1, exit1) = RunRunner(null, withExtra, seedOnly);

            // [THEN] Both AL tests passed: the extra-dependency group saw 3 rows and the
            // seed-only group saw 2. Under a key that ignores the dependency set, the seed-only
            // group restores the 3-row baseline and fails with "expected 2 seeded row(s), found 3".
            Assert.True(Count(out1, "1P/0F/0E") >= 2,
                $"expected both app groups to pass, got:\n{out1}");
            Assert.Equal(0, exit1);

            // [THEN] Two fresh computations, no reuse of either kind, two distinct entries.
            Assert.Equal(2, Count(out1, "InstallBaseline.DepCompanyCache MISS"));
            Assert.Equal(0, Count(out1, "InstallBaseline.DepCompanyCache HIT"));
            Assert.Equal(0, Count(out1, "InstallBaseline.DepCompanyCache DISK-HIT"));
            var written = WriteDigests(out1);
            Assert.Equal(2, written.Count);
            Assert.Equal(2, WrittenPaths(out1).Count);

            // ── process 2: same inputs, same cache root ──
            var (out2, exit2) = RunRunner(null, withExtra, seedOnly);

            Assert.True(Count(out2, "1P/0F/0E") >= 2,
                $"expected both app groups to pass on the warm run, got:\n{out2}");
            Assert.Equal(0, exit2);

            // [THEN] Nothing recomputed or rewritten; both entries restored from disk with the
            // digests process 1 recorded.
            Assert.Equal(0, Count(out2, "InstallBaseline.DepCompanyCache MISS"));
            Assert.Equal(0, Count(out2, "InstallBaseline.DepCompanyCache HIT"));
            Assert.Empty(WriteDigests(out2));
            var hits = HitDigests(out2);
            Assert.Equal(2, hits.Count);
            foreach (var (key, digest) in written)
            {
                Assert.True(hits.ContainsKey(key), $"key {key} was written but not restored:\n{out2}");
                Assert.Equal(digest, hits[key]);
            }
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
