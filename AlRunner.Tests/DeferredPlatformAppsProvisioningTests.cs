// Issue #2232: a real `platform`/`application` floor with no Microsoft reference in the AL
// must not force the platform-apps download (or the offline exit-2 refusal), and a bundle
// that DOES need them must still get today's refusal, with no stray compile diagnostics.
//
// The fixtures declare only a `platform` floor (never `application`,
// .claude/rules/no-base-app-in-csharp-tests.md). A platform floor alone synthesises the
// Microsoft/System root and reaches the same refusal ("missing ...: System"), and the
// deferral treats the two implicit roots identically (ProvisioningCheck.CanDeferPlatformApps).
using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

public sealed class DeferredPlatformAppsProvisioningTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private const string RefusalText = "declares Microsoft dependencies";
    private const string DeferredGreenNote = "ran without the Microsoft platform apps";

    private static string RealServiceTierDir()
    {
        var version = AlRunner.Infrastructure.BcArtifacts.EngineBuiltVersion()
            ?? throw new InvalidOperationException("EngineBuiltVersion() unavailable.");
        var home = Environment.GetEnvironmentVariable("HOME")
            ?? throw new InvalidOperationException("HOME not set on this machine.");
        return Path.Combine(TestArtifacts.StandardCacheDir(home), version.ToString());
    }

    private static string WriteBundle(string dir, int id, string testBody, string extraVars = "")
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        {
          "id": "{{Guid.NewGuid()}}",
          "name": "Deferred Platform Apps {{id}}",
          "publisher": "Repro2232",
          "version": "1.0.0.0",
          "dependencies": [],
          "idRanges": [ { "from": {{id}}, "to": {{id + 9}} } ],
          "platform": "27.0.0.0",
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(dir, "Tests.Codeunit.al"), $$"""
        table {{id}} "Dpa Row {{id}}"
        {
            fields { field(1; "No."; Integer) { } field(2; Txt; Text[30]) { } }
            keys { key(PK; "No.") { Clustered = true; } }
        }

        codeunit {{id + 1}} "Dpa Tests {{id}}"
        {
            Subtype = Test;

            [Test]
            procedure TheTest()
            var
                Row: Record "Dpa Row {{id}}";
                {{extraVars}}
            begin
                {{testBody}}
            end;
        }
        """);
        return dir;
    }

    private static (string Output, int Exit) RunIsolated(string bundleDir, string scratchRoot)
    {
        var realServiceTierDir = RealServiceTierDir();
        TestArtifacts.SkipIf(!Directory.Exists(realServiceTierDir),
            $"real BC service-tier dir not provisioned at '{realServiceTierDir}'.");
        var isolatedHome = Path.Combine(scratchRoot, "home");
        Directory.CreateDirectory(isolatedHome);

        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append($" --artifact-path \"{realServiceTierDir}\"");
        args.Append($" \"{bundleDir}\"");
        args.Append($" --cache \"{Path.Combine(scratchRoot, "al-out")}\"");
        // Never created: a genuinely cold platform-apps search set.
        args.Append($" --package-cache \"{Path.Combine(isolatedHome, "no-such-package-cache")}\"");
        args.Append(" --no-auto-provision");
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
        psi.Environment["HOME"] = isolatedHome;
        // The deferral marks its own child with this; a value inherited from an outer run
        // would make this process the child and skip the gate unconditionally.
        psi.Environment.Remove(AlRunner.Infrastructure.ProvisioningCheck.DeferredPlatformAppsEnvVar);

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

    private static void WithScratch(string name, Action<string> body)
    {
        var scratchRoot = TestScratch.Dir(name);
        try { body(scratchRoot); }
        finally { try { Directory.Delete(scratchRoot, recursive: true); } catch { } }
    }

    /// <summary>The issue's shape: a real floor, AL that names nothing Microsoft, no platform
    /// apps on disk and no network allowed. It runs, green, and says what it ran without.</summary>
    [SkippableFact]
    public void RealFloor_NoMicrosoftReference_ColdCache_NoAutoProvision_RunsWithoutThePlatformApps()
    {
        TestArtifacts.SkipIfMissing();
        WithScratch("al-runner-2232-green", scratch =>
        {
            var bundle = WriteBundle(Path.Combine(scratch, "bundle"), 61980, """
                Row."No." := 7; Row.Txt := 'seven'; Row.Insert(true);
                Row.Get(7);
                if Row.Txt <> 'seven' then
                    Error('expected seven, got %1', Row.Txt);
                """);

            var (output, exit) = RunIsolated(bundle, scratch);

            Assert.True(exit == 0, $"expected a green run without the platform apps. exit={exit}\n{output}");
            Assert.Contains("pass:        1", output);
            Assert.Contains(DeferredGreenNote, output);
            Assert.DoesNotContain(RefusalText, output);
        });
    }

    /// <summary>A platform floor whose AL names a system table the System app supplies: the
    /// attempt without it cannot come out green, so the run ends in today's refusal, and the
    /// discarded attempt's AL0185 never reaches the user.</summary>
    [SkippableFact]
    public void RealFloor_ReferencesSystemTable_ColdCache_NoAutoProvision_StillRefuses()
    {
        TestArtifacts.SkipIfMissing();
        WithScratch("al-runner-2232-needs-system", scratch =>
        {
            var bundle = WriteBundle(Path.Combine(scratch, "bundle"), 61982, """
                Obj.SetRange("Object Type", Obj."Object Type"::Table);
                if Obj.IsEmpty() then
                    Error('no tables');
                """, extraVars: "Obj: Record AllObj;");

            var (output, exit) = RunIsolated(bundle, scratch);

            Assert.True(exit == 2, $"a bundle that needs System must still refuse. exit={exit}\n{output}");
            Assert.Contains(RefusalText, output);
            Assert.Contains("System", output);
            Assert.DoesNotContain("AL0185", output);
            Assert.DoesNotContain(DeferredGreenNote, output);
        });
    }

    /// <summary>Only an all-green attempt is trusted. A failing test compiled without the
    /// platform apps could be failing BECAUSE they are absent (RecordRef.Open by id, for one),
    /// so the verdict falls back to the declared floor: here, today's refusal.</summary>
    [SkippableFact]
    public void RealFloor_NoMicrosoftReference_FailingTest_ColdCache_NoAutoProvision_StillRefuses()
    {
        TestArtifacts.SkipIfMissing();
        WithScratch("al-runner-2232-red", scratch =>
        {
            var bundle = WriteBundle(Path.Combine(scratch, "bundle"), 61984, """
                Row."No." := 1; Row.Insert();
                Error('DPA-DELIBERATE-FAILURE-2232');
                """);

            var (output, exit) = RunIsolated(bundle, scratch);

            Assert.True(exit == 2, $"a non-green attempt without the platform apps must not be the verdict. exit={exit}\n{output}");
            Assert.Contains(RefusalText, output);
            Assert.DoesNotContain("DPA-DELIBERATE-FAILURE-2232", output);
            Assert.DoesNotContain(DeferredGreenNote, output);
        });
    }
}
