// AlOutputCacheDoNotCacheTests — issue #2954. What the AL-output cache does when the key's
// inputs are UNKNOWN rather than merely awkward.
//
// The defect, measured rather than reasoned about
// -----------------------------------------------
// `ProgramSupport.ResolveOrderedDepIds` (was `GetOrderedDepIds`) wraps the whole dependency
// resolve in one try. Its catch used to fold the failure into the key:
//
//     return new[] { $"unresolved:{ex.GetType().Name}:{ex.Message}" };
//
// with a comment arguing that "an unresolvable closure is its own cache identity". It is not.
// An exception type and message are deterministic across runs, so that is ONE fixed string
// standing in for "the closure is unknown" — and, crucially, the run does not abort.
//
// Measured on the fixture below (a two-suite bundle whose second suite has a malformed
// app.json, so it becomes an orphan app group that still compiles while
// `ReadBundleDependencyRoots` throws on it):
//
//     → 2P/0F/0E across 2 tests
//     [cache] WROTE key=898ae2f7…  (12800 bytes)
//     [cache] WROTE key=3648b3fa…  (12800 bytes)
//
// and, with a dependency package rewritten to different bytes at the SAME declared identity
// between two runs, `--print-cache-key` printed 898ae2f7… both times — the same key it printed
// for the same bundle with no dependency declared at all. The key was blind to the entire
// closure, the run was green, and the exit code was 0. A warm cache then serves the DLL
// compiled against the other closure. That is the cache-poisoning shape #2754/#2846/#2955 each
// removed one layer at a time, reintroduced by the error path of the very function #2754 fixed.
//
// Why the answer is not a better term
// -----------------------------------
// There is no better term available: the input was never read. #2954's framing — the honest
// statement is "this run cannot compute a cache identity", and the honest consequence is not to
// consult or write the cache for it. So the degraded paths report a REASON, and both cache
// gates (the CLI loop and server mode) answer it by computing no key at all, which skips the
// read and the write together because every read and both write sites are guarded on the key
// being non-null.
//
// What each arm is for
// --------------------
// Arm 1 is the proof the entry is not WRITTEN, which a key-comparison test cannot give.
// Arm 2 is the control: a healthy bundle must still write and still HIT warm, or the fix would
// pass by disabling the cache outright — the failure mode that would cost far more than the
// exposure it closes (the cost objection recorded on #2954 itself).
// Arm 3 is the poisoning arm: the pre-fix runner printed one identical key for two different
// dependency closures.
// Arm 4 pins that `--print-cache-key` reports a deliberate refusal AS one, instead of as flag
// misuse.
// Arms 5-7 are the unit halves, for the two degraded paths a real run cannot reach after #2987
// (a package that cannot be hashed, and a runner that cannot identify itself). #2954's own
// review comment sets that bar: driven by a test rather than shipped unexercised.
//
// Runner-only claim, deliberately NOT upstream. AL-output cache HIT/MISS is named in
// .claude/rules/bc-behavior-tests-go-upstream.md as a runner-specific claim — it says nothing
// about what Business Central does, and no service tier can adjudicate it. It is also not
// expressible as a `tests/runner-extras/` AL bundle: the assertion is about the CONTENTS OF A
// CACHE DIRECTORY ACROSS TWO RUNNER INVOCATIONS, which AL running inside one invocation cannot
// observe. Hence C#.

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class AlOutputCacheDoNotCacheTests : IDisposable
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private const string SuiteAId = "d0102954-aaaa-4b22-8c33-d44455566677";
    private const string SuiteBId = "d0102954-bbbb-4b22-8c33-d44455566677";
    private const string DepZId = "d0102954-dddd-4b22-8c33-d44455566677";
    private const string DepPublisher = "Fabrikam ISV";
    private const string DepName = "Fabrikam Dep Z";
    private const string DepVersion = "1.0.0.0";

    private readonly string _scratch;

    public AlOutputCacheDoNotCacheTests()
    {
        _scratch = TestScratch.Dir("al-runner-do-not-cache");
        Directory.CreateDirectory(_scratch);
    }

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch { }
    }

    // ── arm 1: the write is refused ───────────────────────────────────────────────────────

    /// <summary>
    /// A run whose dependency closure could not be resolved must write NO AL-output cache
    /// entry — and must still run its tests. Both halves matter: refusing the cache is only
    /// correct if the run itself is unaffected, and a fix that made this bundle fail would be
    /// trading a silent wrong answer for a loud broken one.
    ///
    /// <para>Counting <c>*.dll</c> in the cache root is the assertion a key comparison cannot
    /// make. Before this fix the same run left two of them there.</para>
    /// </summary>
    [SkippableFact]
    public void UnresolvableClosure_WritesNoCacheEntry_AndTheRunStillPasses()
    {
        TestArtifacts.SkipIfMissing();
        var (bundle, pkgDir, cacheDir) = Arrange("write-refused", siblingManifestValid: false);

        var run = RunBundle(bundle, pkgDir, cacheDir);

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("pass:        2", run.Output);
        Assert.Contains("fail:        0", run.Output);

        var written = Directory.GetFiles(cacheDir, "*.dll");
        Assert.True(written.Length == 0,
            "a run that could not compute a cache identity still published "
            + $"{written.Length} AL-output cache entr(y/ies): {string.Join(", ", written.Select(Path.GetFileName))}"
            + $"\n{run.Output}");
        // Nothing to replay from either — a sidecar without its DLL is dead weight, and its
        // presence would mean only half the write was refused.
        Assert.Empty(Directory.GetFiles(cacheDir, "*.enum-registry.json"));

        // Loud, and it must say WHY: a cache silently switching itself off is the shape
        // .claude/rules/loud-failures.md exists to stop.
        Assert.Contains("[cache] NOKEY", run.Output);
        Assert.Contains("neither consulted nor written", run.Output);
        Assert.Contains("could not be resolved", run.Output);
        // And it must not have been counted as an ordinary MISS: a MISS is an entry the next
        // run HITs, and nothing was written for a next run to find.
        Assert.DoesNotContain("[cache] MISS", run.Output);
    }

    // ── arm 2: the control — a healthy bundle is untouched ────────────────────────────────

    /// <summary>
    /// The control this fix cannot ship without. The same fixture with a VALID sibling manifest
    /// must still write entries on the cold run and HIT them on the warm one.
    ///
    /// <para>Without it, an implementation that simply never cached anything would pass arm 1,
    /// and that implementation is strictly worse than the defect: #2954 records the cost
    /// direction as a full emit and compile for the whole bundle, roughly two orders of
    /// magnitude more than the exposure being closed.</para>
    /// </summary>
    [SkippableFact]
    public void HealthyBundle_StillWritesAndServesACacheEntry()
    {
        TestArtifacts.SkipIfMissing();
        var (bundle, pkgDir, cacheDir) = Arrange("healthy-control", siblingManifestValid: true);

        var cold = RunBundle(bundle, pkgDir, cacheDir);
        Assert.Equal(0, cold.ExitCode);
        Assert.DoesNotContain("[cache] NOKEY", cold.Output);
        Assert.Contains("[cache] WROTE", cold.Output);
        Assert.Equal(2, Directory.GetFiles(cacheDir, "*.dll").Length);

        var warm = RunBundle(bundle, pkgDir, cacheDir);
        Assert.Equal(0, warm.ExitCode);
        Assert.Contains("[cache] HIT", warm.Output);
        Assert.DoesNotContain("[cache] MISS", warm.Output);
        Assert.Contains("pass:        2", warm.Output);
    }

    // ── arm 3: the poisoning is gone ──────────────────────────────────────────────────────

    /// <summary>
    /// Two runs of one bundle whose dependency package is REPLACED between them with different
    /// bytes at the same declared identity, while the closure cannot be resolved.
    ///
    /// <para>Before the fix both runs printed the SAME 64-char key (<c>898ae2f7…</c>, which was
    /// also the key for the same bundle with the dependency removed entirely), so the second
    /// run HIT and executed the DLL compiled against the first package. Now neither run has a
    /// key at all — which is the only honest answer, because neither run could describe what it
    /// compiled against.</para>
    ///
    /// <para>The control half is deliberately in the same test: with the sibling manifest valid,
    /// the same two package bodies produce two DIFFERENT keys. Without it this arm would pass
    /// against an implementation that never produced a key for anything.</para>
    /// </summary>
    [SkippableFact]
    public void UnresolvableClosure_DifferentDependencyBytes_ProduceNoKeyRatherThanOneSharedKey()
    {
        TestArtifacts.SkipIfMissing();

        // Degraded: no key, either time.
        var (badBundle, badPkg, badCache) = Arrange("poison-degraded", siblingManifestValid: false);
        WriteSyntheticApp(badPkg, payloadFill: (byte)'X');
        var badFirst = RunPrintCacheKey(badBundle, badPkg, badCache);
        WriteSyntheticApp(badPkg, payloadFill: (byte)'Q');
        var badSecond = RunPrintCacheKey(badBundle, badPkg, badCache);

        Assert.True(badFirst.Key == null && badSecond.Key == null,
            "a bundle whose dependency closure could not be resolved still produced a cache key "
            + $"(first={badFirst.Key ?? "<none>"}, second={badSecond.Key ?? "<none>"}), so two "
            + "different dependency closures can still share one entry:\n" + badFirst.Output);
        Assert.Contains("NO AL-output cache key", badFirst.Output);

        // Control: resolvable, and the two package bodies are genuinely distinguished.
        var (okBundle, okPkg, okCache) = Arrange("poison-control", siblingManifestValid: true);
        WriteSyntheticApp(okPkg, payloadFill: (byte)'X');
        var okFirst = RunPrintCacheKey(okBundle, okPkg, okCache);
        WriteSyntheticApp(okPkg, payloadFill: (byte)'Q');
        var okSecond = RunPrintCacheKey(okBundle, okPkg, okCache);

        Assert.NotNull(okFirst.Key);
        Assert.NotNull(okSecond.Key);
        Assert.True(okFirst.Key != okSecond.Key,
            "two dependency packages with different bytes at the same declared identity hashed "
            + $"to the same AL-output cache key ({okFirst.Key}):\n" + okSecond.Output);
    }

    // ── arm 4: --print-cache-key reports a refusal as a refusal ───────────────────────────

    /// <summary>
    /// <c>--print-cache-key</c> already exited 2 with "found no key to print", explaining the
    /// two ways the flag can be MISUSED (<c>--no-cache</c>, cross-bundle dedup). A deliberate
    /// refusal is neither, and reporting it as one sends the caller looking for a mistake they
    /// did not make while the actionable reason never reaches them.
    /// </summary>
    [SkippableFact]
    public void PrintCacheKey_UnresolvableClosure_SaysThereIsNoKey_NotThatTheFlagWasMisused()
    {
        TestArtifacts.SkipIfMissing();
        var (bundle, pkgDir, cacheDir) = Arrange("print-key", siblingManifestValid: false);

        var run = RunPrintCacheKey(bundle, pkgDir, cacheDir);

        Assert.Null(run.Key);
        Assert.Equal(2, run.ExitCode);
        Assert.Contains("this run has NO AL-output cache key", run.Output);
        Assert.Contains("could not be resolved", run.Output);
        Assert.DoesNotContain("Re-run without --no-cache", run.Output);
    }

    // ── arms 5-7: the unit halves ─────────────────────────────────────────────────────────

    /// <summary>
    /// The degraded dependency term must REPORT itself, naming the package. #2987 made this
    /// branch unreachable through a real run (an unreadable package is no longer indexed, so it
    /// is never resolved), which is exactly why it is driven directly here — the reachability
    /// argument is about the CALLER, and a future caller resolving a dependency some other way
    /// lands on it.
    ///
    /// <para>Both directions, or the arm proves nothing: a package that CAN be hashed must
    /// report no degradation at all, otherwise an implementation that reported every dependency
    /// as degraded would pass — and that implementation disables the cache for every run.</para>
    /// </summary>
    [Fact]
    public void DependencyContentTerm_ReportsDegradation_OnlyWhenItCannotHashThePackage()
    {
        var dir = Path.Combine(_scratch, "term-report");
        Directory.CreateDirectory(dir);
        var real = WriteSyntheticApp(dir, payloadFill: (byte)'X');

        var hashable = new List<string>();
        var term = ProgramSupport.DependencyContentTerm(real, hashable.Add);
        Assert.StartsWith("sha256:", term);
        Assert.Empty(hashable);

        var missing = Path.Combine(dir, "never-written.app");
        var degraded = new List<string>();
        var missingTerm = ProgramSupport.DependencyContentTerm(missing, degraded.Add);
        Assert.StartsWith("unhashable:", missingTerm);
        Assert.EndsWith(":absent", missingTerm);
        var reason = Assert.Single(degraded);
        // Names its OWN package: a reason that did not would leave the operator unable to tell
        // which dependency switched the cache off.
        Assert.Contains(Path.GetFullPath(missing), reason);
        Assert.Contains("could not be hashed", reason);
    }

    /// <summary>
    /// The same shape one layer up, and the one place still passing
    /// <see cref="RunnerFingerprint.UnknownContentHash"/> into a PERSISTED key.
    /// <see cref="RunnerFingerprint.WriteKeyLines(Action{string})"/> writes
    /// <c>runner:&lt;hash&gt;</c> into both the AL-output key and the source-workspace key; with
    /// the sentinel that line reads <c>runner:unknown</c>, identical for every runner build that
    /// ever lands in that state (a single-file publish, an in-memory host, the DLL unlinked
    /// under a running process). <c>AppLoader</c>'s manifest index (#2987) and its r2r-chunk
    /// cache (#2955) already refuse the sentinel; this closes the third.
    /// </summary>
    [Fact]
    public void RunnerFingerprint_UnknownContentHash_IsAReasonNotToCache()
    {
        Assert.Null(RunnerFingerprint.UncacheableReasonFor(
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"));

        foreach (var degraded in new[] { RunnerFingerprint.UnknownContentHash, "", null })
        {
            var reason = RunnerFingerprint.UncacheableReasonFor(degraded);
            Assert.NotNull(reason);
            // Says which line would have been wrong, so the reason is diagnosable without
            // reading this test.
            Assert.Contains($"runner:{RunnerFingerprint.UnknownContentHash}", reason);
        }

        // And the live property agrees for the assembly actually running, which is on disk —
        // if this ever flipped, every test in this suite would be running uncached and nothing
        // else would say so.
        Assert.Null(RunnerFingerprint.UncacheableReason);
    }

    /// <summary>
    /// <c>AlOutputCacheBlocker</c> is the single place the CLI gate, the server-mode gate and
    /// <c>--print-cache-key</c> read, so they cannot drift apart on what "uncacheable" means.
    /// It must pass a dependency-side reason through, and answer null when there is none.
    /// </summary>
    [Fact]
    public void AlOutputCacheBlocker_PassesADependencyReasonThrough_AndIsNullOtherwise()
    {
        var clean = new ProgramSupport.OrderedDependencyIds(new[] { "dep-a", "dep-b" }, null);
        Assert.Null(ProgramSupport.AlOutputCacheBlocker(clean));

        var blocked = new ProgramSupport.OrderedDependencyIds(
            new[] { "unresolved:MissingDependencyException:Dependency not found" },
            "the dependency closure of '/x' could not be resolved");
        Assert.Equal("the dependency closure of '/x' could not be resolved",
            ProgramSupport.AlOutputCacheBlocker(blocked));
    }

    // ── arm 8: the SERVER-MODE gate, which arms 1-7 never entered (#3262) ─────────────────

    /// <summary>
    /// Issue #3262. Arms 1-7 above drive the CLI gate, <c>--print-cache-key</c> and the two
    /// helpers as units. Not one of them starts <c>--server</c>, so the server-mode half of the
    /// gate — <c>Program.cs</c>'s <c>serverCacheBlocker</c> branch — was reachable code that no
    /// test entered in either direction. Deleting it left all seven green.
    ///
    /// <para><b>Why that is a real exposure and not a symmetry nit.</b> The CLI gate and the
    /// server-mode gate write into the SAME <c>&lt;cacheDir&gt;</c>. A server request that wrote
    /// an entry under a key blind to its dependency closure is served to a later CLI run against
    /// that same directory, and the other way round. So a regression dropping only the
    /// server-mode half reintroduces the whole of #2954 for CLI runs too, through a poisoned
    /// shared directory, with every one of arms 1-7 still passing.</para>
    ///
    /// <para><b>Both directions in one arm, deliberately.</b> The blocked half alone would pass
    /// against a server that never cached anything — the exact "would this still pass against a
    /// do-nothing implementation?" failure in <c>.claude/rules/tdd.md</c> that this issue is
    /// about. Re-shipping that here would be worse than the gap it closes. So the healthy half
    /// runs the same fixture with a valid sibling manifest and must still write an entry cold
    /// and still HIT it warm.</para>
    ///
    /// <para><b>The cache decision is read from the phase log, not from stderr.</b> Server mode
    /// emits no <c>[cache] WROTE</c> or <c>[cache] HIT</c> line at all — those two
    /// <c>Console.Error</c> calls exist only on the CLI path — so an stderr grep for them would
    /// assert nothing and would keep passing whatever the server did. <c>AL_RUNNER_PHASE_LOG</c>
    /// records <c>cache_hits</c> / <c>cache_misses</c> per bundle row, which is the server's
    /// actual decision rather than a proxy for it, and is machine-readable.</para>
    ///
    /// <para>Runner-specific, not a BC claim — see this file's header. Server-mode process
    /// configuration and AL-output cache HIT/MISS say nothing about what Business Central does,
    /// and no service tier can adjudicate either, so this belongs in <c>AlRunner.Tests</c>
    /// rather than upstream in the corpus.</para>
    /// </summary>
    [SkippableFact]
    public async Task ServerMode_UnresolvableClosure_WritesNoCacheEntry_WhileAHealthyBundleStillCaches()
    {
        TestArtifacts.SkipIfMissing();

        // ── blocked half: the closure cannot be resolved ──────────────────────────────────
        var (badBundle, badPkg, badCache) = Arrange("server-write-refused", siblingManifestValid: false);
        var bad = await RunServerBundleAsync(badBundle, badPkg, badCache, "server-nokey");

        // The run itself is unaffected. Refusing the cache is only the right answer if the
        // tests still execute — a fix that made this request fail would trade a silent wrong
        // answer for a loud broken one.
        Assert.Equal(2, bad.Summary.GetProperty("total").GetInt32());
        Assert.Equal(2, bad.Summary.GetProperty("passed").GetInt32());
        Assert.Equal(0, bad.Summary.GetProperty("failed").GetInt32());
        Assert.Equal(0, bad.Summary.GetProperty("errors").GetInt32());

        // THE assertion the seven existing arms could not make: nothing was published into the
        // directory a later CLI run would read. Counting files, not comparing keys — a key
        // comparison cannot tell you whether a write happened.
        var badWritten = Directory.GetFiles(badCache, "*.dll");
        Assert.True(badWritten.Length == 0,
            "a SERVER request that could not compute a cache identity still published "
            + $"{badWritten.Length} AL-output cache entr(y/ies) into a directory shared with CLI "
            + $"runs: {string.Join(", ", badWritten.Select(Path.GetFileName))}\n{bad.Diagnostics}");
        // A sidecar without its DLL would mean only half the write was refused.
        Assert.Empty(Directory.GetFiles(badCache, "*.enum-registry.json"));

        // Loud, naming the reason, and tagged as the SERVER line specifically: the CLI gate
        // emits "  [<rel>] [cache] NOKEY", so asserting the bare "[cache] NOKEY" text would
        // also match a CLI emission and could not tell the two gates apart.
        Assert.Contains("[server]", bad.StdErr);
        Assert.Contains("[cache] NOKEY", bad.StdErr);
        Assert.Contains("neither consulted nor written", bad.StdErr);
        Assert.Contains("could not be resolved", bad.StdErr);

        // Never accounted as an ordinary MISS: a MISS is an entry the next run HITs, and
        // nothing was written for a next run to find. Read from the phase log, so this is the
        // server's own recorded decision and not the absence of a line it never prints.
        Assert.Equal(0, bad.CacheHits);
        Assert.Equal(0, bad.CacheMisses);

        // ── healthy half: the control, same fixture, valid sibling manifest ───────────────
        var (okBundle, okPkg, okCache) = Arrange("server-healthy-control", siblingManifestValid: true);

        var cold = await RunServerBundleAsync(okBundle, okPkg, okCache, "server-cold");
        Assert.Equal(2, cold.Summary.GetProperty("passed").GetInt32());
        Assert.DoesNotContain("[cache] NOKEY", cold.StdErr);
        Assert.True(cold.CacheMisses > 0,
            $"the control's cold run recorded no cache MISS, so it never reached the cache at "
            + $"all and the warm assertion below would prove nothing\n{cold.Diagnostics}");
        Assert.Equal(0, cold.CacheHits);
        var okWritten = Directory.GetFiles(okCache, "*.dll");
        Assert.True(okWritten.Length > 0,
            "the control half wrote NO cache entry, so the blocked half above proves nothing — "
            + $"an implementation that simply never caches would satisfy it\n{cold.Diagnostics}");

        // Warm: a FRESH server process against the same directory must SERVE what the cold one
        // wrote. A second request to the same process would be answered from the in-process
        // module cache without consulting the on-disk cache this arm is about.
        var warm = await RunServerBundleAsync(okBundle, okPkg, okCache, "server-warm");
        Assert.Equal(2, warm.Summary.GetProperty("passed").GetInt32());
        Assert.DoesNotContain("[cache] NOKEY", warm.StdErr);
        Assert.True(warm.CacheHits > 0,
            $"the control's warm run did not HIT the entry its cold run wrote, so the "
            + $"server-mode cache is off for every bundle and the blocked half is vacuous"
            + $"\n{warm.Diagnostics}");
    }

    // ── fixture ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A two-suite bundle plus a package cache holding one third-party dependency.
    ///
    /// <para><paramref name="siblingManifestValid"/> false writes suite B's app.json as
    /// truncated JSON. That is what makes the closure unresolvable while the run still
    /// succeeds: <c>InProcessAppPackager.ReadIdentity</c> answers null so
    /// <c>BuildAppGroups</c> folds the suite into the orphan app group and compiles it, while
    /// <c>ReadBundleDependencyRoots</c> parses the same file and throws — the two sides of the
    /// asymmetry this issue lives in. No <c>application</c> or <c>platform</c> property on
    /// either manifest, so the resolved closure is entirely under this test's control
    /// (.claude/rules/no-base-app-in-csharp-tests.md).</para>
    /// </summary>
    private (string Bundle, string PkgDir, string CacheDir) Arrange(string name, bool siblingManifestValid)
    {
        var root = Path.Combine(_scratch, name);
        var suiteA = Path.Combine(root, "bundle", "suiteA");
        var suiteB = Path.Combine(root, "bundle", "suiteB");
        var pkgDir = Path.Combine(root, "pkg");
        var cacheDir = Path.Combine(root, "cache");
        Directory.CreateDirectory(suiteA);
        Directory.CreateDirectory(suiteB);
        Directory.CreateDirectory(pkgDir);
        Directory.CreateDirectory(cacheDir);

        File.WriteAllText(Path.Combine(suiteA, "app.json"), $$"""
        {
          "id": "{{SuiteAId}}",
          "name": "DoNotCache Probe A",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [
            { "id": "{{DepZId}}", "name": "{{DepName}}", "publisher": "{{DepPublisher}}", "version": "{{DepVersion}}" }
          ],
          "idRanges": [ { "from": 60795, "to": 60796 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(suiteA, "ProbeA.Codeunit.al"), """
        codeunit 60795 "DoNotCache Probe A"
        {
            Subtype = Test;

            [Test]
            procedure ProbeAWorks()
            begin
            end;
        }
        """);

        File.WriteAllText(Path.Combine(suiteB, "app.json"), siblingManifestValid
            ? $$"""
              {
                "id": "{{SuiteBId}}",
                "name": "DoNotCache Probe B",
                "publisher": "AL Runner",
                "version": "1.0.0.0",
                "dependencies": [],
                "idRanges": [ { "from": 60796, "to": 60797 } ],
                "runtime": "14.0"
              }
              """
            // Truncated mid-string: JsonReaderException, deterministic message.
            : """{ "id": "not valid json """);
        File.WriteAllText(Path.Combine(suiteB, "ProbeB.Codeunit.al"), """
        codeunit 60796 "DoNotCache Probe B"
        {
            Subtype = Test;

            [Test]
            procedure ProbeBWorks()
            begin
            end;
        }
        """);

        WriteSyntheticApp(pkgDir, payloadFill: (byte)'X');
        return (Path.Combine(root, "bundle"), pkgDir, cacheDir);
    }

    /// <summary>
    /// A minimal NAVX <c>.app</c> with a fixed-size uncompressed payload, so two packages with
    /// the same declared identity can differ in bytes. Every zip entry carries an explicit
    /// timestamp — <c>ZipArchive</c> stamps <c>DateTimeOffset.Now</c> otherwise, which would
    /// make even a byte-for-byte rewrite produce different bytes and rob the control arms of
    /// their meaning.
    /// </summary>
    private static string WriteSyntheticApp(string dir, byte payloadFill)
    {
        var xml = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/navx/2015/manifest">
              <App Id="{DepZId}" Name="{DepName}" Publisher="{DepPublisher}" Version="{DepVersion}"/>
              <Dependencies />
            </Package>
            """;
        var entryStamp = new DateTimeOffset(2021, 6, 7, 8, 9, 10, TimeSpan.Zero);
        using var ms = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(
                   ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            var manifest = zip.CreateEntry("NavxManifest.xml", System.IO.Compression.CompressionLevel.NoCompression);
            manifest.LastWriteTime = entryStamp;
            using (var es = manifest.Open())
                es.Write(Encoding.UTF8.GetBytes(xml));

            var payload = zip.CreateEntry("payload.bin", System.IO.Compression.CompressionLevel.NoCompression);
            payload.LastWriteTime = entryStamp;
            using (var ps = payload.Open())
            {
                var buf = new byte[4096];
                buf.AsSpan().Fill(payloadFill);
                ps.Write(buf);
            }
        }
        var zipBytes = ms.ToArray();
        var result = new byte[8 + zipBytes.Length];
        result[0] = (byte)'N'; result[1] = (byte)'A'; result[2] = (byte)'V'; result[3] = (byte)'X';
        BitConverter.TryWriteBytes(result.AsSpan(4, 4), (uint)8);
        zipBytes.CopyTo(result, 8);
        var path = Path.Combine(dir, $"{DepPublisher}_{DepName}_{DepVersion}.app");
        File.WriteAllBytes(path, result);
        return path;
    }

    // ── runner invocation ─────────────────────────────────────────────────────────────────

    private static (int ExitCode, string Output) RunBundle(string bundle, string pkgDir, string cacheDir)
    {
        var r = Spawn(bundle, pkgDir, cacheDir, extra: " --verbose");
        return (r.ExitCode, r.Output);
    }

    private static (string? Key, int ExitCode, string Output) RunPrintCacheKey(
        string bundle, string pkgDir, string cacheDir)
    {
        var r = Spawn(bundle, pkgDir, cacheDir, extra: " --print-cache-key");
        var m = Regex.Match(r.Output, @"\[cache\]\s+KEY\s+key=([0-9a-f]{64})");
        return (m.Success ? m.Groups[1].Value : null, r.ExitCode, r.Output);
    }

    /// <summary>
    /// One <c>--server</c> spawn, one <c>runTests</c> request over <paramref name="bundle"/>,
    /// returning the protocol-v2 summary, the process's whole stderr, and the cache decision
    /// the server actually recorded for that bundle.
    ///
    /// <para>A fresh process per call is the point, not overhead. The warm half of arm 8 has to
    /// prove the entry survives to a LATER run, and a second request to the same process is
    /// answered from the in-process cross-bundle module cache without ever consulting the
    /// on-disk AL-output cache the assertion is about.</para>
    ///
    /// <para><paramref name="phaseTag"/> gives each call its own <c>AL_RUNNER_PHASE_LOG</c>
    /// file, so <c>cache_hits</c> / <c>cache_misses</c> are this request's and not a running
    /// total across the three spawns.</para>
    ///
    /// <para>The process is shut down BEFORE stderr is read. The summary line arriving on
    /// stdout establishes nothing about how much of the request's stderr the background drain
    /// task has appended (the two-pipe race <see cref="CliServer.StdErrSinceAsync"/>
    /// documents), and arm 8's negative assertions on stderr are unsound until the stream is
    /// closed and drained. Disposal waits for exit, which is EOF on stderr.</para>
    /// </summary>
    private async Task<ServerCacheObservation> RunServerBundleAsync(
        string bundle, string pkgDir, string cacheDir, string phaseTag)
    {
        var phaseLog = Path.Combine(_scratch, $"{phaseTag}.phase.jsonl");
        var server = await CliServer.StartAsync(
            new[] { "--cache", cacheDir, "--package-cache", pkgDir, "--verbose" },
            extraEnv: new Dictionary<string, string> { ["AL_RUNNER_PHASE_LOG"] = phaseLog });

        JsonElement summary;
        try
        {
            var req = JsonSerializer.Serialize(new
            {
                command = "runTests",
                sourcePaths = new[] { bundle },
                packagePaths = new[] { pkgDir },
            });
            var lines = await server.SendRequestStreamingAsync(req, TimeSpan.FromSeconds(240));
            (_, summary) = ProtocolV2Streaming.Split(lines);
        }
        finally
        {
            await server.DisposeAsync();
        }
        var stdErr = server.StdErr;

        // Sum across bundle rows rather than taking a single row: a request carries one
        // sourcePaths entry here, but summing states the claim in a form that cannot silently
        // read only the first of several.
        var bundleRows = File.Exists(phaseLog)
            ? File.ReadAllLines(phaseLog)
                .Where(l => l.Length > 0)
                .Select(l => JsonDocument.Parse(l).RootElement)
                .Where(e => e.TryGetProperty("kind", out var k) && k.GetString() == "bundle")
                .ToList()
            : new List<JsonElement>();
        Assert.True(bundleRows.Count > 0,
            $"no phase-log bundle row for '{phaseTag}' — the cache-decision assertions below "
            + $"would read 0/0 and pass vacuously.\n--- stderr ---\n{stdErr}");

        var hits = bundleRows.Sum(r => r.TryGetProperty("cache_hits", out var h) ? h.GetInt32() : 0);
        var misses = bundleRows.Sum(r => r.TryGetProperty("cache_misses", out var m) ? m.GetInt32() : 0);
        return new ServerCacheObservation(summary, stdErr, hits, misses);
    }

    /// <summary>What one server request did about the AL-output cache, and the evidence.</summary>
    private sealed record ServerCacheObservation(
        JsonElement Summary, string StdErr, int CacheHits, int CacheMisses)
    {
        /// <summary>Everything an assertion failure needs, assembled only when one fails.</summary>
        public string Diagnostics =>
            $"cache_hits={CacheHits} cache_misses={CacheMisses}\n--- stderr ---\n{StdErr}";
    }

    private static (int ExitCode, string Output) Spawn(
        string bundle, string pkgDir, string cacheDir, string extra)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        args.Append($" \"{bundle}\"");
        args.Append($" --package-cache \"{pkgDir}\"");
        args.Append($" --cache \"{cacheDir}\"");
        args.Append(extra);
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = args.ToString(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = RepoRoot,
        };
        var sb = new StringBuilder();
        using var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(240_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        lock (sb) return (p.ExitCode, sb.ToString());
    }
}
