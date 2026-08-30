using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// #2178 — a source-compiled dependency that itself depends on another source-compiled
/// app must be able to see it.
///
/// Both source pre-passes in <c>Program.cs</c> write each app they build into its own
/// deterministic workspace dir (a synthetic source-only <c>.app</c> plus the
/// <c>*.symbols.json</c> sidecar that carries the compile half), and both topologically
/// sort so a dependency is built before its dependent. Neither one used to feed those
/// freshly written dirs back into the NEXT app's compile: every iteration resolved
/// against the same list that was computed once, before the loop. So the moment a chain
/// was three apps deep — <c>test -&gt; middle -&gt; base</c>, with both <c>middle</c> and
/// <c>base</c> compiled from AL source in the same invocation — the middle app could not
/// see the base app at all:
/// <code>
///   [layered] WROTE LSC Chain Base 1.0.0.0 -> AL_Runner_LSC_Chain_Base_1_0_0_0.app
///   &lt;layered-deps&gt;: COMPILE-FAIL — [layered] Failed to emit symbols for impl
///     'LSC Chain Middle' ...: Dependency not found: AL Runner/LSC Chain Base v1.0.0.0
///     ... Searched: &lt;platform-apps&gt;, &lt;test-apps&gt;
/// </code>
/// naming an app the runner had written itself one line earlier.
///
/// Two apps was the deepest shape with coverage, and two apps never exercises it: the one
/// impl in a two-app bundle has no impl dependency of its own. The existing
/// <c>dep-tableext-platform-base-{dep,main}</c> fixture passes for exactly that reason.
///
/// The fixtures below use no Microsoft objects at all (platform/application 1.0.0.0, no
/// declared Microsoft deps), so the chain is legitimately resolvable with whatever package
/// caches this machine happens to have — the claim under test is about propagation between
/// the runner's OWN workspace dirs, not about artifact provisioning.
///
/// Spawns the real runner; needs the BC artifact cache. Skips (visibly) when absent.
/// </summary>
public class LayeredSourceChainTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    // ── Fixture writers ────────────────────────────────────────────────────────
    // One chain, written three times into different layouts so each pre-pass sees the
    // shape it is responsible for. Object IDs are per-app (each app.json declares its
    // own idRanges) and the app ids are fresh GUIDs per run, so no workspace-deps /
    // compiled-deps / al-out cache from a previous run can answer for any of them.

    private static void WriteBaseApp(string dir, Guid baseId)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        {
          "id": "{{baseId}}",
          "name": "LSC Chain Base",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "application": "1.0.0.0",
          "idRanges": [ { "from": 60050, "to": 60059 } ],
          "runtime": "14.0"
        }
        """);
        // A table AND a codeunit: the table is what the middle app needs at COMPILE time
        // (it declares `Record "LSC Chain Row"`), the codeunit is what it needs at RUNTIME.
        // A fix that restored only one of the two halves would fail the other.
        File.WriteAllText(Path.Combine(dir, "ChainRow.Table.al"), """
        table 60050 "LSC Chain Row"
        {
            DataClassification = CustomerContent;
            fields
            {
                field(1; "Row Code"; Code[20]) { DataClassification = CustomerContent; }
                field(2; "Row Value"; Integer) { DataClassification = CustomerContent; }
                field(3; "Row Label"; Text[50]) { DataClassification = CustomerContent; }
            }
            keys { key(PK; "Row Code") { Clustered = true; } }
        }
        """);
        File.WriteAllText(Path.Combine(dir, "ChainBaseApi.Codeunit.al"), """
        codeunit 60051 "LSC Chain Base Api"
        {
            procedure BaseTag(): Text[50]
            begin
                exit('chain-base-60051');
            end;

            procedure Amplify(Value: Integer): Integer
            begin
                exit((Value * 2) + 3);
            end;
        }
        """);
    }

    private static void WriteMiddleApp(string dir, Guid middleId, Guid baseId)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        {
          "id": "{{middleId}}",
          "name": "LSC Chain Middle",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [
            { "id": "{{baseId}}", "name": "LSC Chain Base", "publisher": "AL Runner", "version": "1.0.0.0" }
          ],
          "platform": "1.0.0.0",
          "application": "1.0.0.0",
          "idRanges": [ { "from": 60060, "to": 60069 } ],
          "runtime": "14.0"
        }
        """);
        // Public surface is primitives-only on purpose: the test app depends on THIS app
        // and on nothing else, so a green run cannot be explained by the test app seeing
        // the base app directly.
        File.WriteAllText(Path.Combine(dir, "ChainMiddleApi.Codeunit.al"), """
        codeunit 60060 "LSC Chain Middle Api"
        {
            procedure StoreAmplified(RowCode: Code[20]; Value: Integer)
            var
                ChainRow: Record "LSC Chain Row";
                BaseApi: Codeunit "LSC Chain Base Api";
            begin
                ChainRow.Init();
                ChainRow."Row Code" := RowCode;
                ChainRow."Row Value" := BaseApi.Amplify(Value);
                ChainRow."Row Label" := BaseApi.BaseTag();
                ChainRow.Insert();
            end;

            procedure ReadValue(RowCode: Code[20]): Integer
            var
                ChainRow: Record "LSC Chain Row";
            begin
                ChainRow.Get(RowCode);
                exit(ChainRow."Row Value");
            end;

            procedure ReadLabel(RowCode: Code[20]): Text[50]
            var
                ChainRow: Record "LSC Chain Row";
            begin
                ChainRow.Get(RowCode);
                exit(ChainRow."Row Label");
            end;
        }
        """);
    }

    private static void WriteTestApp(string dir, Guid testsId, Guid middleId)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        {
          "id": "{{testsId}}",
          "name": "LSC Chain Test",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [
            { "id": "{{middleId}}", "name": "LSC Chain Middle", "publisher": "AL Runner", "version": "1.0.0.0" }
          ],
          "platform": "1.0.0.0",
          "application": "1.0.0.0",
          "idRanges": [ { "from": 60070, "to": 60079 } ],
          "runtime": "14.0"
        }
        """);
        // Three tests, each pinning a different half of the handoff:
        //  - Amplified: the base app's CODE ran ((11*2)+3 = 25, not 11 and not 0).
        //  - Marker:    a text only the base app's codeunit can produce round-tripped
        //               through the base app's TABLE.
        //  - Negative:  a missing row still raises BC's real record-not-found error
        //               rather than degrading to a default 0.
        File.WriteAllText(Path.Combine(dir, "ChainTests.al"), """
        codeunit 60070 "LSC Chain Tests"
        {
            Subtype = Test;

            [Test]
            procedure ChainStore_AmplifiesThroughBaseCodeunit()
            var
                MiddleApi: Codeunit "LSC Chain Middle Api";
                Actual: Integer;
            begin
                MiddleApi.StoreAmplified('LSC-A', 11);
                Actual := MiddleApi.ReadValue('LSC-A');
                if Actual <> 25 then
                    Error('Expected 25 ((11*2)+3 through the base app''s codeunit), got %1', Actual);
            end;

            [Test]
            procedure ChainStore_BaseTagMarkerRoundTrips()
            var
                MiddleApi: Codeunit "LSC Chain Middle Api";
                Actual: Text;
            begin
                MiddleApi.StoreAmplified('LSC-B', 4);
                Actual := MiddleApi.ReadLabel('LSC-B');
                if Actual <> 'chain-base-60051' then
                    Error('Expected ''chain-base-60051'' from the base app''s codeunit, got ''%1''', Actual);
            end;

            [Test]
            procedure ChainRead_UnknownRowCodeRaises()
            var
                MiddleApi: Codeunit "LSC Chain Middle Api";
                Actual: Text;
            begin
                asserterror MiddleApi.ReadValue('LSC-NOPE');
                Actual := GetLastErrorText();
                if not Actual.Contains('does not exist') then
                    Error('Expected a record-not-found error from the base app''s table, got ''%1''', Actual);
            end;
        }
        """);
    }

    private static (string Output, int Exit) RunRunner(string scratchRoot, params string[] bundles)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        foreach (var b in bundles) args.Append($" \"{b}\"");
        args.Append($" --cache \"{Path.Combine(scratchRoot, "al-out")}\"");
        var platformApps = TestArtifacts.PlatformAppsDir();
        if (Directory.Exists(platformApps)) args.Append($" --package-cache \"{platformApps}\"");
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet", Arguments = args.ToString(),
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
        };
        var sb = new StringBuilder();
        using var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(300_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        lock (sb) return (sb.ToString(), p.ExitCode);
    }

    private static string NewScratch(string tag) =>
        Path.Combine(Path.GetTempPath(), "al-runner-layered-chain", tag, Guid.NewGuid().ToString("N"));

    // ── Positive: RunLayeredPrePass (three bundle arguments) ───────────────────

    /// <summary>
    /// Three source bundles on one command line, chained test -&gt; middle -&gt; base.
    /// Both middle and base are "impls" in <c>RunLayeredPrePass</c> terms, and middle is
    /// the first impl whose own dependency is also an impl — the shape that had no
    /// coverage. Before the fix this never reached a test: exit 3 with
    /// "Dependency not found: AL Runner/LSC Chain Base".
    /// </summary>
    [SkippableFact]
    public void LayeredPrePass_ThreeSourceBundles_MiddleImplResolvesTheBaseImpl()
    {
        TestArtifacts.SkipIfMissing();

        var scratch = NewScratch("layered");
        Guid baseId = Guid.NewGuid(), middleId = Guid.NewGuid(), testsId = Guid.NewGuid();
        var baseDir = Path.Combine(scratch, "base-app");
        var middleDir = Path.Combine(scratch, "middle-app");
        var testsDir = Path.Combine(scratch, "tests-app");
        WriteBaseApp(baseDir, baseId);
        WriteMiddleApp(middleDir, middleId, baseId);
        WriteTestApp(testsDir, testsId, middleId);

        var (output, exit) = RunRunner(scratch, baseDir, middleDir, testsDir);

        Assert.DoesNotContain("Dependency not found", output);
        Assert.DoesNotContain("AL1022", output);
        Assert.DoesNotContain("COMPILE-FAIL", output);
        Assert.True(exit == 0 && output.Contains("3P/0F/0E"),
            $"a three-deep source chain must compile and run (exit {exit}):\n{output}");
    }

    // ── Positive: BuildSiblingSourceDeps (one bundle argument, sibling sources) ─

    /// <summary>
    /// The same chain through the OTHER pre-pass: only the test app is passed as a
    /// bundle, and middle + base are discovered as sibling source apps next to it.
    /// <c>BuildSiblingSourceDeps</c> topo-sorts for the same reason
    /// <c>RunLayeredPrePass</c> does, and had the same gap — its <c>resolveDirs</c> was
    /// computed once, before the loop, and never gained the workspace dirs it was
    /// writing. Without this case a fix applied to only one of the two pre-passes would
    /// still ship the bug.
    /// </summary>
    [SkippableFact]
    public void SiblingSourceDeps_ChainedSourceApps_MiddleSiblingResolvesTheBaseSibling()
    {
        TestArtifacts.SkipIfMissing();

        var scratch = NewScratch("sibling");
        Guid baseId = Guid.NewGuid(), middleId = Guid.NewGuid(), testsId = Guid.NewGuid();
        var baseDir = Path.Combine(scratch, "base-app");
        var middleDir = Path.Combine(scratch, "middle-app");
        var testsDir = Path.Combine(scratch, "tests-app");
        WriteBaseApp(baseDir, baseId);
        WriteMiddleApp(middleDir, middleId, baseId);
        WriteTestApp(testsDir, testsId, middleId);

        // Only the TEST app is a bundle; the other two are siblings under the same parent.
        var (output, exit) = RunRunner(scratch, testsDir);

        Assert.DoesNotContain("Dependency not found", output);
        Assert.DoesNotContain("AL1022", output);
        Assert.DoesNotContain("COMPILE-FAIL", output);
        Assert.True(exit == 0 && output.Contains("3P/0F/0E"),
            $"a three-deep sibling source chain must compile and run (exit {exit}):\n{output}");
    }

    // ── Negative: an absent dependency still fails, and says which one ─────────

    /// <summary>
    /// The mirror of the two cases above: with the base app genuinely absent — not
    /// passed as a bundle, not a sibling, not in any package cache — the middle app must
    /// still fail, and the failure must name the app it could not find. Widening the
    /// search set is only correct if it stops widening at "what actually exists"; a fix
    /// that made resolution succeed unconditionally would turn this into a green run
    /// with a silently missing dependency.
    /// </summary>
    [SkippableFact]
    public void LayeredPrePass_BaseAppAbsent_FailsNamingTheMissingDependency()
    {
        TestArtifacts.SkipIfMissing();

        var scratch = NewScratch("absent");
        Guid baseId = Guid.NewGuid(), middleId = Guid.NewGuid(), testsId = Guid.NewGuid();
        // The base app is written OUTSIDE the scratch tree so it is neither a bundle
        // argument nor a sibling of the two apps that are.
        var chainRoot = Path.Combine(scratch, "chain");
        var middleDir = Path.Combine(chainRoot, "middle-app");
        var testsDir = Path.Combine(chainRoot, "tests-app");
        WriteMiddleApp(middleDir, middleId, baseId);
        WriteTestApp(testsDir, testsId, middleId);

        var (output, exit) = RunRunner(scratch, middleDir, testsDir);

        Assert.NotEqual(0, exit);
        Assert.Contains("LSC Chain Base", output);
        Assert.Contains("Dependency not found", output);
    }
}
