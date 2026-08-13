using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Issue #1683 — a real app + its separate test app (the README's "usual shape",
/// `al-runner MyApp MyApp.Test`) passed as two bundle args in ONE invocation. The dep
/// app has a table-event subscriber that fires during its own install trigger (a
/// common setup-table pattern). Bundle 1 compiles the dep app as its OWN emitted
/// module; bundle 2 (the test app) resolves the SAME dep app as an external package
/// dependency via the layered pre-pass + DependencyLoader, which — before the fix —
/// re-emitted/re-compiled it into a SECOND, distinct module. The event-subscription
/// registry then paired a subscriber MethodInfo discovered from one module's Type
/// with a subscriberInstance BC's own dispatcher materialized from the OTHER module's
/// Type, and `RuntimeMethodInfo.Invoke` threw `TargetException: Object does not match
/// target type` inside `NavEventScope.CallEventSubscriberInternalAsync` →
/// `ValidateInvokeTarget`.
///
/// RED (pre-fix): the run aborts with a bundle-level EXEC-FAIL naming
/// "TargetException: Object does not match target type" and the test app's own test
/// never gets to assert anything.
/// GREEN (post-fix): one loaded module per AL app identity — the dep app's own-bundle
/// module is reused as the test app's dependency, so the install trigger's Modify()
/// fires the real (single, matched) subscriber cleanly and the test app's test passes.
///
/// Spawns the real runner; needs the BC artifact cache. Skips (no-op) when absent.
/// See DefineFlagIntegrationTests for why this used to be
/// [Collection("server-serial")] and no longer is — #1809.
/// </summary>
public class CrossBundleModuleIdentityDedupTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private static (string output, int exit) RunRunner(params string[] bundles)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        foreach (var b in bundles) args.Append(" \"").Append(b).Append('"');
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet", Arguments = args.ToString(),
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
        };
        var sb = new StringBuilder();
        var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(180_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        lock (sb) return (sb.ToString(), p.ExitCode);
    }

    [SkippableFact]
    public void DepAppOwnBundlePlusDependentTestApp_InstallTriggerSubscriberFiresCleanly()
    {
        TestArtifacts.SkipIfMissing();

        var root = Path.Combine(Path.GetTempPath(), "al-runner-xbundle-dedup", Guid.NewGuid().ToString("N"));
        var depDir = Path.Combine(root, "dep-app");
        var testDir = Path.Combine(root, "test-app");
        Directory.CreateDirectory(depDir);
        Directory.CreateDirectory(testDir);

        var depId = "a1b2c3d4-1683-4a1b-9c3d-000000000001";

        File.WriteAllText(Path.Combine(depDir, "app.json"), $$"""
        {
          "id": "{{depId}}",
          "name": "TE Dep App 1683",
          "publisher": "Repro1683",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "application": "1.0.0.0",
          "idRanges": [ { "from": 61860, "to": 61869 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(depDir, "TeDep.al"), """
        table 61860 "TE Setup 1683"
        {
            DataClassification = SystemMetadata;
            fields
            {
                field(1; "Primary Key"; Code[10]) { }
                field(2; "Value"; Integer) { }
            }
            keys { key(PK; "Primary Key") { Clustered = true; } }
        }

        codeunit 61861 "TE Subscriber 1683"
        {
            [EventSubscriber(ObjectType::Table, Database::"TE Setup 1683", 'OnAfterModifyEvent', '', false, false)]
            local procedure OnAfterModifyTeSetup(var Rec: Record "TE Setup 1683")
            begin
            end;
        }

        codeunit 61862 "TE Install 1683"
        {
            Subtype = Install;
            trigger OnInstallAppPerCompany()
            var
                Setup: Record "TE Setup 1683";
            begin
                if not Setup.Get('X') then begin
                    Setup."Primary Key" := 'X';
                    Setup.Insert();
                end;
                Setup.Value += 1;
                Setup.Modify(true); // fires OnAfterModifyEvent -> "TE Subscriber 1683"
            end;
        }
        """);

        File.WriteAllText(Path.Combine(testDir, "app.json"), $$"""
        {
          "id": "b2c3d4e5-1683-4a1b-9c3d-000000000002",
          "name": "TE Main Tests 1683",
          "publisher": "Repro1683",
          "version": "1.0.0.0",
          "dependencies": [
            { "id": "{{depId}}", "name": "TE Dep App 1683", "publisher": "Repro1683", "version": "1.0.0.0" }
          ],
          "platform": "1.0.0.0",
          "application": "1.0.0.0",
          "idRanges": [ { "from": 61870, "to": 61879 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(testDir, "TeMainTests.al"), """
        codeunit 61870 "TE Main Tests 1683"
        {
            Subtype = Test;

            [Test]
            procedure DepInstallTriggerRan()
            var
                Setup: Record "TE Setup 1683";
            begin
                if not Setup.Get('X') then
                    Error('TE Setup "X" missing - dep install trigger did not run');
                if Setup.Value < 1 then
                    Error('TE Setup "X" Value is %1 - Modify(true) did not commit', Setup.Value);
            end;
        }
        """);

        var (output, exitCode) = RunRunner(depDir, testDir);

        // The exact defect: two loaded modules for one AL app identity produced a
        // subscriber MethodInfo/instance mismatch. Never silently pass a run that hit
        // this — the assertion below is the whole point of the RED→GREEN cycle.
        Assert.DoesNotContain("TargetException", output);
        Assert.DoesNotContain("Object does not match target type", output);
        // The test app's own test must actually have run and passed — not merely
        // "no crash". 1P/0F/0E is TestExecutor's own per-bundle summary line.
        Assert.Contains("1P/0F/0E", output);
        Assert.Equal(0, exitCode);
    }
}
