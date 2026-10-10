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

    private static (string Output, int Exit) RunIsolated(string bundleDir, string scratchRoot, params string[] extraArgs)
    {
        var r = RunIsolatedSplit(bundleDir, scratchRoot, extraArgs);
        return (r.Merged, r.Exit);
    }

    /// <summary>The same run with stdout and stderr kept apart, for the tests whose claim is WHICH
    /// stream a line is on (#5477). <c>Merged</c> is the interleaving <see cref="RunIsolated"/> reports.</summary>
    private static (string Stdout, string Stderr, string Merged, int Exit) RunIsolatedSplit(
        string bundleDir, string scratchRoot, params string[] extraArgs)
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
        foreach (var a in extraArgs) args.Append($" {a}");
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

        var merged = new StringBuilder();
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        using var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (merged) { merged.AppendLine(e.Data); stdout.AppendLine(e.Data); } };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (merged) { merged.AppendLine(e.Data); stderr.AppendLine(e.Data); } };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(300_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        lock (merged) return (stdout.ToString(), stderr.ToString(), merged.ToString(), p.ExitCode);
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
            Assert.Contains("passed 1 ", output);
            Assert.Contains(DeferredGreenNote, output);
            Assert.DoesNotContain(RefusalText, output);
        });
    }

    /// <summary>
    /// #4481: the green attempt child re-runs the invocation from the top, so it re-emits the whole
    /// startup preamble, and the parent had already printed its own copy before deciding to defer.
    /// Replaying the child's capture whole therefore printed every queued startup line twice
    /// (today, at default verbosity, the run header). The user must see it exactly once. Counted
    /// over the quiet run most users take.
    /// </summary>
    [SkippableFact]
    public void ColdDeferral_GreenAttempt_PrintsTheStartupPreambleExactlyOnce()
    {
        TestArtifacts.SkipIfMissing();
        WithScratch("al-runner-4481-cold", scratch =>
        {
            var bundle = WriteBundle(Path.Combine(scratch, "bundle"), 61990, """
                Row."No." := 7; Row.Txt := 'seven'; Row.Insert(true);
                Row.Get(7);
                if Row.Txt <> 'seven' then
                    Error('expected seven, got %1', Row.Txt);
                """);

            // An explicit --expectations dir is what makes `[expectations] loaded` print at default
            // verbosity, so a second queued line rides the same replay.
            var expectationsDir = Path.Combine(scratch, "expectations");
            Directory.CreateDirectory(expectationsDir);

            var (output, exit) = RunIsolated(bundle, scratch, $"--expectations \"{expectationsDir}\"");

            Assert.True(exit == 0, $"expected a green deferred run. exit={exit}\n{output}");
            // The replay happened (this is the deferral path, not a run with the apps present).
            Assert.Contains(DeferredGreenNote, output);
            var lines = output.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
            // The run header (`al-runner <ver> · BC <build> · N app`) prints at default verbosity;
            // `[bc] selected BC` prints only under --verbose, so it is bounded rather than required.
            int Count(Func<string, bool> match) => lines.Count(match);
            var header = Count(l => l.StartsWith("al-runner ", StringComparison.Ordinal) && l.Contains(" · BC "));
            Assert.True(header == 1,
                $"the run header printed {header} time(s), expected exactly 1: the attempt child "
                + $"re-prints the preamble the parent already showed (#4481).\n{output}");
            var selected = Count(l => l.StartsWith("[bc] selected BC", StringComparison.Ordinal));
            Assert.True(selected <= 1, $"`[bc] selected BC` printed {selected} time(s).\n{output}");
            var expectations = Count(l => l.StartsWith("[expectations] loaded", StringComparison.Ordinal));
            Assert.True(expectations == 1,
                $"`[expectations] loaded` printed {expectations} time(s), expected 1 (#4481).\n{output}");
            Assert.Contains("passed 1 ", output);
        });
    }

    private const string GreenBody = """
        Row."No." := 7; Row.Txt := 'seven'; Row.Insert(true);
        Row.Get(7);
        if Row.Txt <> 'seven' then
            Error('expected seven, got %1', Row.Txt);
        """;

    /// <summary>
    /// #5477: <c>--output-json</c> contracts stdout to hold the JSON document and nothing else.
    /// The cold deferral replayed the attempt child's stdout through <c>Console.Out</c>, which the
    /// parent has already pointed at stderr for this mode, so stdout came out EMPTY and the document
    /// landed on stderr with exit 0. Both directions are pinned: the document is on stdout, parses as
    /// one JSON value, and records the run's outcome; and nothing diagnostic shares that stream.
    /// </summary>
    [SkippableFact]
    public void ColdDeferral_GreenAttempt_OutputJson_StdoutIsExactlyTheJsonDocument()
    {
        TestArtifacts.SkipIfMissing();
        WithScratch("al-runner-5477-json", scratch =>
        {
            var bundle = WriteBundle(Path.Combine(scratch, "bundle"), 61992, GreenBody);

            var (stdout, stderr, merged, exit) = RunIsolatedSplit(bundle, scratch, "--output-json");

            Assert.True(exit == 0, $"expected a green deferred run. exit={exit}\n{merged}");
            // The deferral path, not a run with the apps present, and its note stays a diagnostic.
            Assert.Contains(DeferredGreenNote, stderr);
            Assert.DoesNotContain(DeferredGreenNote, stdout);
            Assert.False(string.IsNullOrWhiteSpace(stdout),
                $"--output-json left stdout empty on the deferred path (#5477).\nstderr:\n{stderr}");
            using var doc = System.Text.Json.JsonDocument.Parse(stdout);
            Assert.Equal(System.Text.Json.JsonValueKind.Object, doc.RootElement.ValueKind);
            // The document describes THIS run: its test, passed, and the exit code the process took.
            Assert.Equal(0, doc.RootElement.GetProperty("exitCode").GetInt32());
            Assert.Contains("TheTest", stdout);
            // And the document is not ALSO on stderr: one copy, on the contracted stream.
            Assert.DoesNotContain("\"exitCode\"", stderr);
        });
    }

    /// <summary>
    /// #5477, the sibling shape: under <c>--verbose</c> the parent has already printed lines directly
    /// (outside the queued startup preamble), and the attempt child prints the same ones again; the
    /// replay put both in front of the user. Each such line must show once. Deliberately NOT "no line
    /// repeats": a verbose run legitimately repeats its own internal logs within one process, and two
    /// processes each boot a runtime and say so.
    /// </summary>
    [SkippableFact]
    public void ColdDeferral_GreenAttempt_Verbose_DirectlyPrintedLinesAppearOnce()
    {
        TestArtifacts.SkipIfMissing();
        WithScratch("al-runner-5477-verbose", scratch =>
        {
            var bundle = WriteBundle(Path.Combine(scratch, "bundle"), 61994, GreenBody);

            var (output, exit) = RunIsolated(bundle, scratch, "--verbose");

            Assert.True(exit == 0, $"expected a green deferred run. exit={exit}\n{output}");
            Assert.Contains(DeferredGreenNote, output);
            var lines = output.Split('\n').Select(l => l.TrimEnd('\r').Trim()).Where(l => l.Length > 0).ToList();
            // Printed by Program.cs's own top-level flow, before the deferral decision.
            string[] startupNarrative = ["[Cecil]", "[reexec]", "[bc]", "[expectations]", "al-runner ", "package caches"];
            var narrative = lines.Where(l => startupNarrative.Any(p => l.StartsWith(p, StringComparison.Ordinal))).ToList();
            Assert.Contains(narrative, l => l.StartsWith("package caches (requested)", StringComparison.Ordinal));
            var repeated = narrative.GroupBy(l => l).Where(g => g.Count() > 1)
                .Select(g => $"{g.Count()}x  {g.Key}").ToList();
            Assert.True(repeated.Count == 0,
                "these verbose startup lines were printed more than once on the deferred path (#5477):\n"
                + string.Join("\n", repeated) + $"\n--- full output ---\n{output}");
            // The marker that makes this possible is protocol between the two processes, never output.
            Assert.DoesNotContain("deferred-attempt-begin", output);
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

    /// <summary>--no-strict-exit makes a red run exit 0, so an attempt under it cannot report
    /// "not green" and must not be made at all. Asserted on the messages: the refusal's own exit
    /// code bypasses the strict-exit mapping today, and may not tomorrow.</summary>
    [SkippableFact]
    public void RealFloor_FailingTest_NoStrictExit_IsNotDeferred()
    {
        TestArtifacts.SkipIfMissing();
        WithScratch("al-runner-2232-nostrict", scratch =>
        {
            var bundle = WriteBundle(Path.Combine(scratch, "bundle"), 61986, """
                Row."No." := 1; Row.Insert();
                Error('DPA-DELIBERATE-FAILURE-2232');
                """);

            var (output, exit) = RunIsolated(bundle, scratch, "--no-strict-exit");

            Assert.Contains(RefusalText, output);
            Assert.DoesNotContain(DeferredGreenNote, output);
            Assert.DoesNotContain("DPA-DELIBERATE-FAILURE-2232", output);
        });
    }
}
