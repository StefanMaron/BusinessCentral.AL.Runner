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
///      BC's <c>NavValue.GetBytes()</c> produces, plus the isolated-storage / record-link /
///      auto-increment state. Two independent fresh computations do NOT produce the same
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
/// Every case also asserts the app group's own AL test still passes against REAL
/// Company-Initialize-seeded data (Company Information's singleton row), so a "cache" that
/// restored nothing at all would fail the assertion rather than merely run fast.
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
    /// A two-app closure whose dependency set is unique to this test invocation: the main app
    /// depends on a private dependency app whose id AND source text carry a fresh GUID, so the
    /// dependency assembly it compiles to has an MVID no other run has ever produced — and
    /// therefore an InstallTriggerRunner.CurrentDependencySetKey(), and an on-disk baseline
    /// entry, that starts out guaranteed absent. Without that these tests could not tell a
    /// genuine first-run MISS from a hit on an entry some earlier run left behind.
    /// </summary>
    private static (string dep, string main) WriteUniqueClosure(string root, int baseId, string tag)
    {
        var depId = Guid.NewGuid().ToString();
        var marker = Guid.NewGuid().ToString("N");
        var depDir = Path.Combine(root, tag + "-dep");
        var mainDir = Path.Combine(root, tag + "-main");

        Directory.CreateDirectory(depDir);
        File.WriteAllText(Path.Combine(depDir, "app.json"), $$"""
        {
          "id": "{{depId}}",
          "name": "IBDisk {{tag}} Dep",
          "publisher": "InstallBaselineDiskTest",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "application": "1.0.0.0",
          "idRanges": [ { "from": {{baseId}}, "to": {{baseId + 4}} } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(depDir, "Dep.al"), $$"""
        codeunit {{baseId}} "IBDisk {{tag}} Dep Cu"
        {
            procedure Marker(): Text
            begin
                exit('{{marker}}');
            end;
        }
        """);

        Directory.CreateDirectory(mainDir);
        File.WriteAllText(Path.Combine(mainDir, "app.json"), $$"""
        {
          "id": "{{Guid.NewGuid()}}",
          "name": "IBDisk {{tag}} Main",
          "publisher": "InstallBaselineDiskTest",
          "version": "1.0.0.0",
          "dependencies": [
            { "id": "{{depId}}", "name": "IBDisk {{tag}} Dep", "publisher": "InstallBaselineDiskTest", "version": "1.0.0.0" }
          ],
          "platform": "1.0.0.0",
          "application": "1.0.0.0",
          "idRanges": [ { "from": {{baseId + 5}}, "to": {{baseId + 9}} } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(mainDir, "Tests.al"), $$"""
        codeunit {{baseId + 5}} "IBDisk {{tag}} Test"
        {
            Subtype = Test;

            [Test]
            procedure CompanyInitializeSeededRealData()
            var
                CompanyInformation: Record "Company Information";
            begin
                // [THEN] Whether this app group computed the dependency+company baseline or
                // restored it from disk, the real Base App codeunit 2 "Company-Initialize"
                // result is present: Company Information's singleton row exists. A disk
                // restore that dropped rows fails here rather than merely being fast.
                CompanyInformation.Get();
            end;
        }
        """);
        return (depDir, mainDir);
    }

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

        var root = Path.Combine(Path.GetTempPath(), "al-runner-ib-disk-roundtrip", Guid.NewGuid().ToString("N"));
        try
        {
            var (dep, main) = WriteUniqueClosure(root, 62000, "rt");

            var (out1, exit1) = RunRunner(null, dep, main);
            Assert.Equal(0, exit1);
            Assert.True(Count(out1, "1P/0F/0E") >= 1, $"main app group should pass, got:\n{out1}");

            // [THEN] First process: this closure has never been seen, so its key is computed
            // fresh and persisted — and it is NOT among the keys this run restored from disk.
            // (The bundle's plain MS-platform app group may legitimately hit an entry an
            // earlier run left; the assertion is about THIS closure's key, not about the
            // process having no hits at all.)
            var written = WriteDigests(out1);
            Assert.True(written.Count >= 1, $"expected at least one DISK-WRITE, got:\n{out1}");
            Assert.Empty(HitDigests(out1).Keys.Intersect(written.Keys));
            Assert.True(Count(out1, "InstallBaseline.DepCompanyCache MISS") >= 1,
                $"expected at least one fresh computation, got:\n{out1}");

            var (out2, exit2) = RunRunner(null, dep, main);
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

        var root = Path.Combine(Path.GetTempPath(), "al-runner-ib-disk-killswitch", Guid.NewGuid().ToString("N"));
        try
        {
            var (dep, main) = WriteUniqueClosure(root, 62020, "ks");

            // Seed an entry that the kill-switched run would hit if the switch did nothing.
            var (out1, exit1) = RunRunner(null, dep, main);
            Assert.Equal(0, exit1);
            var paths = WrittenPaths(out1);
            Assert.True(paths.Count >= 1, $"expected the first run to write an entry, got:\n{out1}");
            var before = paths.ToDictionary(p => p, File.ReadAllBytes);

            var (out2, exit2) = RunRunner(
                new Dictionary<string, string> { ["AL_RUNNER_NO_DEP_COMPANY_CACHE"] = "1" }, dep, main);

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

        var root = Path.Combine(Path.GetTempPath(), "al-runner-ib-disk-corrupt", Guid.NewGuid().ToString("N"));
        try
        {
            var (dep, main) = WriteUniqueClosure(root, 62040, "cx");

            var (out1, exit1) = RunRunner(null, dep, main);
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

            var (out2, exit2) = RunRunner(null, dep, main);

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
            var (out3, exit3) = RunRunner(null, dep, main);
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

    // ── 4. scoping: a different dependency closure gets a different file ───────────────

    [SkippableFact]
    public void DifferentDependencyClosures_GetDifferentKeysAndDifferentFiles()
    {
        TestArtifacts.SkipIfMissing();

        var root = Path.Combine(Path.GetTempPath(), "al-runner-ib-disk-scope", Guid.NewGuid().ToString("N"));
        try
        {
            var (depA, mainA) = WriteUniqueClosure(root, 62060, "sa");
            var (depB, mainB) = WriteUniqueClosure(root, 62080, "sb");

            var (output, exit) = RunRunner(null, depA, mainA, depB, mainB);

            Assert.Equal(0, exit);
            Assert.True(Count(output, "1P/0F/0E") >= 2,
                $"expected both main app groups to pass, got:\n{output}");

            // [THEN] Two closures that differ by one dependency assembly produced two distinct
            // cache keys and two distinct files. A key that ignored the dependency set (or a
            // path that collided) would show one.
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
}
