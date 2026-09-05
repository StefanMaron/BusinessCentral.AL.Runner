// WatchCrossBundleModuleIdentityTests — issue #2594.
//
// `al-runner <app> <app>.Test --watch` is the README's usual shape, and until this test it ran
// a path no test covered: the cross-bundle module-identity dedup (#1683) was gated OFF under
// `--watch`, at both ends.
//
//   * the READ side, `DependencyLoader.TryGetByAppId(...)` — "was this AppId already loaded?"
//   * the WRITE side, `DependencyLoader.RegisterLoaded(...)` — "this module IS that AppId"
//
// With the write side skipped, bundle 1 compiled the dep app and told nobody, so bundle 2
// resolved the SAME AL app through DependencyLoader's Tier-3 source compile into a SECOND live
// module for one AL identity. That is exactly #1683: the event-subscription registry pairs a
// subscriber MethodInfo discovered from one module's Type with a subscriberInstance BC's own
// dispatcher materialized from the OTHER module's Type, and `RuntimeMethodInfo.Invoke` throws
// `TargetException: Object does not match target type` at
// `NavEventScope.CallEventSubscriberInternalAsync` → `ValidateInvokeTarget`.
//
// MEASURED, NOT INFERRED — the RED this test was written against
// --------------------------------------------------------------
// This exact fixture, driven under `--watch` against the pre-fix build:
//
//   [install-trigger] Codeunit60032.OnInstallAppPerCompany (Dep_Repro2594_…) threw:
//       TargetException: Object does not match target type.
//     at System.Reflection.RuntimeMethodInfo.Invoke(…)
//     at Microsoft.Dynamics.Nav.EventSubscription.NavEventScope.CallEventSubscriberInternalAsync(…)
//     …
//   === test-app — EXEC FAIL ===
//     WM Main Tests 2594: EXEC-FAIL: Object does not match target type.
//
//   Tests: 0 total / pass: 0 — the test bundle's tests never ran at all.
//
// The issue itself recorded that the duplicate module had NOT been observed under `--watch`
// ("Not verified: I have not run a two-bundle --watch session"), and warned it might surface as
// an EMIT-ZERO instead. On this fixture it surfaces as the dispatch failure above.
//
// WHY THE GATE'S OWN RATIONALE NO LONGER HELD
// -------------------------------------------
// The comment disabling it said reuse under `--watch` would replay iteration 1's stale pre-edit
// assembly forever, because `RegisterLoaded` was first-wins. Both halves changed in the #1892
// follow-up and the comment did not:
//
//   * `TryGetByAppId` returns null — deliberately not a reuse — when the cached entry's
//     SourcePath equals the one being asked about. A watch cycle asking about its own SuiteDir
//     therefore always recompiles.
//   * `RegisterLoaded` OVERWRITES on a same-SourcePath re-registration, so a later cycle's
//     freshly compiled module replaces the earlier one rather than losing to it.
//
// `--server`, which is the other warm edit-and-rerun loop and the one those two rules were
// written for, calls both without any gate at all.
//
// BOTH DIRECTIONS, BECAUSE ONE ALONE WOULD BE HALF THE CLAIM
// ----------------------------------------------------------
// Cycle 1 proves the dedup: one module per AL identity across two bundles. Cycle 2 edits the
// DEPENDENCY and proves the reuse is not stale. Without the second half this test would pass
// against a "reuse whatever compiled first, forever" implementation — which is precisely the
// hazard the removed gate claimed to prevent, so leaving it unasserted would be arguing the
// gate was pointless rather than showing it.
//
// The consumer's files are never touched, following WatchCrossAppOverloadRebindTests: a cycle
// is triggered by ONE file write, so there is no window in which the watcher can split an edit
// across two cycles and land the assertion on a half-applied one. The cost is that
// `DepStampIsCurrent` is EXPECTED TO FAIL in cycle 1 — asserted, because it is the measurement
// that the fixture really does start on stamp 1, without which cycle 2 could pass by the answer
// having been 2 all along.
using System.Diagnostics;
using Xunit;

namespace AlRunner.Tests;

public class WatchCrossBundleModuleIdentityTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private const string DepAppId = "c3d4e5f6-2594-4a1b-9c3d-000000000001";
    private const string TestAppId = "d4e5f6a7-2594-4a1b-9c3d-000000000002";

    // Allocated from each fixture app.json's OWN idRanges. 60030-60049 is unused elsewhere in
    // AlRunner.Tests.
    private const int SetupTableId = 60030;
    private const int SubscriberId = 60031;
    private const int InstallId = 60032;
    private const int StampId = 60033;
    private const int TestsId = 60040;

    /// <summary>
    /// The dependency app. Its shape is load-bearing: a table, a subscriber ON that table, and
    /// an install codeunit whose <c>Modify(true)</c> fires that subscriber. That is what turns
    /// "two modules for one identity" from an invisible inefficiency into an observable failure
    /// — the dispatcher has to pair a MethodInfo with an instance, and with two modules live
    /// they come from different Types.
    ///
    /// <para><paramref name="stamp"/> is what the consumer reads back, so a module served from
    /// an earlier cycle cannot pass.</para>
    /// </summary>
    private static void WriteDepSource(string dir, int stamp) =>
        File.WriteAllText(Path.Combine(dir, "Dep.al"), $$"""
        table {{SetupTableId}} "WM Setup 2594"
        {
            DataClassification = SystemMetadata;
            fields
            {
                field(1; "Primary Key"; Code[10]) { }
                field(2; "Value"; Integer) { }
            }
            keys { key(PK; "Primary Key") { Clustered = true; } }
        }

        codeunit {{SubscriberId}} "WM Subscriber 2594"
        {
            [EventSubscriber(ObjectType::Table, Database::"WM Setup 2594", 'OnAfterModifyEvent', '', false, false)]
            local procedure OnAfterModifyWmSetup(var Rec: Record "WM Setup 2594")
            begin
            end;
        }

        codeunit {{InstallId}} "WM Install 2594"
        {
            Subtype = Install;
            trigger OnInstallAppPerCompany()
            var
                Setup: Record "WM Setup 2594";
            begin
                if not Setup.Get('X') then begin
                    Setup."Primary Key" := 'X';
                    Setup.Insert();
                end;
                Setup.Value += 1;
                Setup.Modify(true); // fires OnAfterModifyEvent -> "WM Subscriber 2594"
            end;
        }

        codeunit {{StampId}} "WM Stamp 2594"
        {
            procedure Stamp(): Integer
            begin
                exit({{stamp}});
            end;
        }
        """);

    private static string MakeDepBundle(string root, int stamp)
    {
        var dir = Path.Combine(root, "dep-app");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        {
          "id": "{{DepAppId}}",
          "name": "WM Dep App 2594",
          "publisher": "Repro2594",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": {{SetupTableId}}, "to": {{SetupTableId + 9}} } ],
          "runtime": "14.0"
        }
        """);
        WriteDepSource(dir, stamp);
        return dir;
    }

    /// <summary>
    /// The consumer. Two tests, and both have to be here:
    ///
    /// <para><c>DepInstallTriggerRan</c> can only pass if the dependency's install trigger
    /// completed, which it cannot while the subscriber dispatch throws — this is the #1683
    /// half, and it must be green in BOTH cycles.</para>
    ///
    /// <para><c>DepStampIsCurrent</c> pins the dependency's answer to the value only its
    /// CURRENT compile returns, and reports the value it actually got. Written once, expecting
    /// the post-edit stamp, so the consumer is never rewritten mid-session: it fails in cycle 1
    /// by design and must pass in cycle 2.</para>
    /// </summary>
    private static string MakeTestAppBundle(string root, int expectedStamp)
    {
        var dir = Path.Combine(root, "test-app");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        {
          "id": "{{TestAppId}}",
          "name": "WM Main Tests 2594",
          "publisher": "Repro2594",
          "version": "1.0.0.0",
          "dependencies": [
            { "id": "{{DepAppId}}", "name": "WM Dep App 2594",
              "publisher": "Repro2594", "version": "1.0.0.0" }
          ],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": {{TestsId}}, "to": {{TestsId + 9}} } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(dir, "Tests.al"), $$"""
        codeunit {{TestsId}} "WM Main Tests 2594"
        {
            Subtype = Test;

            [Test]
            procedure DepInstallTriggerRan()
            var
                Setup: Record "WM Setup 2594";
            begin
                if not Setup.Get('X') then
                    Error('WM-SETUP-MISSING: the dependency install trigger did not run');
                if Setup.Value < 1 then
                    Error('WM-SETUP-VALUE=%1: Modify(true) did not commit', Setup.Value);
            end;

            [Test]
            procedure DepStampIsCurrent()
            var
                Stamp: Codeunit "WM Stamp 2594";
            begin
                if Stamp.Stamp() <> {{expectedStamp}} then
                    Error('WM-STAMP=%1', Stamp.Stamp());
            end;
        }
        """);
        return dir;
    }

    /// <summary>
    /// Two bundles under <c>--watch</c>, one declaring the other as a dependency, must resolve
    /// that dependency to the module the dependency's own bundle already compiled — on cycle 1,
    /// and to the CURRENT one after the dependency is edited.
    /// </summary>
    ///
    /// <param name="dependencyFirst">
    /// Which order the two bundles are listed in on the command line. Both are covered because
    /// the freshness half rests on the dependency's own bundle executing FIRST, and that is not
    /// a property of the argument order — <c>--watch</c> reorders bundles dependency-first
    /// (#2614/#2814, Program.cs's `if (watchMode &amp;&amp; bundles.Count > 1)` sort).
    ///
    /// <para>It is load-bearing rather than incidental. `DependencyLoader` has TWO writers of
    /// the cache this fix reads, and they disagree on the SourcePath they record for one AL
    /// identity: `RegisterLoaded` from the bundle loop records the bundle's own SuiteDir, while
    /// `LoadOne`'s Tier-3 path records the resolved `.app` package path. Only the SuiteDir
    /// spelling makes `TryGetByAppId`/`RegisterLoaded`'s same-SourcePath rules fire, so if the
    /// consumer ever ran first and registered the identity under the package path, every later
    /// cycle would keep serving that first module and the edit would go unnoticed. Reversing
    /// the argument order is how that gets exercised; if the sort is ever dropped, this case
    /// goes red instead of the staleness shipping silently.</para>
    /// </param>
    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WatchCycles_ResolveOneModulePerAlIdentity_AndAlwaysTheCurrentOne(bool dependencyFirst)
    {
        TestArtifacts.SkipIfMissing();

        var root = TestScratch.Dir("al-runner-watch-module-identity");
        Directory.CreateDirectory(root);
        var depDir = MakeDepBundle(root, stamp: 1);
        var testAppDir = MakeTestAppBundle(root, expectedStamp: 2);

        // Outside the repository — a --cache pointed inside a worktree has faked a whole-bundle
        // install failure before.
        var cacheDir = Path.Combine(root, "cache");
        Directory.CreateDirectory(cacheDir);

        var lines = new List<CapturedLine>();
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = TestBuildConfig.RunArgs(ProjectPath) + TestBuildConfig.BcVersionArg
                + (dependencyFirst
                    ? $" \"{depDir}\" \"{testAppDir}\""
                    : $" \"{testAppDir}\" \"{depDir}\"")
                + $" --watch --cache \"{cacheDir}\"",
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
        };
        using var p = Process.Start(psi)!;
        void Pump(StreamReader r, OutputStream stream) => _ = Task.Run(async () =>
        {
            string? l;
            while ((l = await r.ReadLineAsync()) != null)
                lock (lines) lines.Add(new CapturedLine(stream, l));
        });
        Pump(p.StandardOutput, OutputStream.Stdout);
        Pump(p.StandardError, OutputStream.Stderr);

        string DumpTail() { lock (lines) return string.Join("\n", lines.TakeLast(80).Select(l => $"[{l.Stream}] {l.Text}")); }

        async Task<int> WaitForMarkerAfter(int fromIndex, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                List<int> found;
                lock (lines)
                    found = WatchOutputSlicing.FindStdoutMarkerIndices(
                        lines, WatchOutputSlicing.WaitingForSourceMarker, fromIndex);
                if (found.Count > 0) return found[0];
                if (p.HasExited)
                {
                    await Task.Delay(500);
                    throw new TimeoutException(
                        $"watch marker not seen — subprocess exited early (exit={p.ExitCode}).\n"
                        + $"--- last output ---\n{DumpTail()}");
                }
                await Task.Delay(200);
            }
            if (p.HasExited) await Task.Delay(500);
            throw new TimeoutException($"watch marker not seen.\n--- last output ---\n{DumpTail()}");
        }

        string Segment(int from, int to) { lock (lines) return WatchOutputSlicing.MergedJoin(lines, from, to); }

        // The #1683 half, asserted the same way in both cycles. Named by the defect rather than
        // only by the outcome, so a cycle that reddens for an unrelated reason cannot be read as
        // this claim holding, and this claim failing cannot be read as something else.
        void AssertOneModulePerIdentity(string cycle, string label)
        {
            Assert.False(cycle.Contains("TargetException", StringComparison.Ordinal),
                $"{label}: the dependency resolved to a SECOND module for an AL identity bundle 1 "
                + "had already loaded, so the subscriber MethodInfo and the instance BC's "
                + "dispatcher materialized came from different Types (#1683/#2594).\n" + cycle);
            Assert.False(cycle.Contains("Object does not match target type", StringComparison.Ordinal),
                $"{label}: the same defect, by its message.\n" + cycle);

            // The install trigger is where the mismatch fires, and it is reported on its own line
            // before any test result — asserted separately so a failed install cannot hide behind
            // a later EXEC-FAIL, and so the EMIT-ZERO shape #2594 warned about is not silently
            // accepted either.
            Assert.False(cycle.Contains("[install-trigger]", StringComparison.Ordinal)
                         && cycle.Contains("threw:", StringComparison.Ordinal),
                $"{label}: the dependency's install trigger threw.\n" + cycle);
            Assert.False(cycle.Contains("EXEC-FAIL", StringComparison.Ordinal),
                $"{label}: the test bundle compiled and loaded and then failed to run, so none of "
                + "its tests were counted.\n" + cycle);
            Assert.False(cycle.Contains("EMIT-ZERO", StringComparison.Ordinal),
                $"{label}: the dependency produced no objects — the other shape #2594 predicted "
                + "for a bundle that has to Tier-3 compile a dependency already loaded.\n" + cycle);

            // Not "no crash": this named test must actually have run and passed. A cycle that
            // discovered zero tests cannot satisfy it.
            Assert.True(cycle.Contains($"PASS  Codeunit{TestsId}.DepInstallTriggerRan", StringComparison.Ordinal),
                $"{label}: DepInstallTriggerRan did not pass — the dependency's install trigger "
                + "never completed, or the test never ran.\n" + cycle);
        }

        try
        {
            // ── Cycle 1. Bundle 1 compiles the dep app; bundle 2 must resolve THAT module rather
            // than compiling its own copy. Pre-fix, this cycle already fails: the install trigger
            // throws TargetException and the test bundle EXEC-FAILs with zero tests counted.
            int m1 = await WaitForMarkerAfter(0, TimeSpan.FromSeconds(300));
            var cycle1 = Segment(0, m1);
            AssertOneModulePerIdentity(cycle1, $"cycle 1 (dependencyFirst: {dependencyFirst})");

            // The fixture genuinely starts on stamp 1. Without this, cycle 2 could pass by the
            // dependency having answered 2 all along, and the freshness claim would be empty.
            Assert.True(cycle1.Contains("WM-STAMP=1", StringComparison.Ordinal),
                "cycle 1 did not report the dependency's pre-edit stamp, so cycle 2 proves "
                + "nothing about the module being re-resolved after the edit:\n" + cycle1);

            // ── Cycle 2. Edit ONLY the dependency. The consumer's files are untouched, so reuse
            // is correct here only if it is reuse of the module compiled THIS cycle — the exact
            // hazard the removed --watch gate cited as its reason to exist.
            WriteDepSource(depDir, stamp: 2);
            int m2 = await WaitForMarkerAfter(m1 + 1, TimeSpan.FromSeconds(300));
            var cycle2 = Segment(m1 + 1, m2);
            AssertOneModulePerIdentity(cycle2, $"cycle 2, dependency edited (dependencyFirst: {dependencyFirst})");

            // The freshness half. A stale module reports its own value, so the failure says which
            // compile answered rather than only that something went wrong.
            Assert.False(cycle2.Contains("WM-STAMP=1", StringComparison.Ordinal),
                "cycle 2: a STALE module was served for the dependency — Stamp() answered with "
                + "cycle 1's value after the dependency's source changed. This is the hazard the "
                + "removed --watch gate named, and RegisterLoaded's same-SourcePath overwrite "
                + "(#1892) is what is supposed to prevent it.\n" + cycle2);
            Assert.True(cycle2.Contains($"PASS  Codeunit{TestsId}.DepStampIsCurrent", StringComparison.Ordinal),
                "cycle 2: DepStampIsCurrent did not pass — the module resolved for the dependency "
                + "did not return the value its current source compiles to.\n" + cycle2);
            Assert.False(cycle2.Contains("FAIL", StringComparison.Ordinal),
                "cycle 2: a warm --watch cycle must answer exactly as a cold run of these sources "
                + "does.\n" + cycle2);
        }
        finally
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
        }
    }
}
