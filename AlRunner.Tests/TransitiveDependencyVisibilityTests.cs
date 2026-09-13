using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// #4096 — a source-compiled app sees the objects of the apps it DECLARES, plus the
/// dependencies those apps propagate, and nothing else. Chain: test -&gt; middle -&gt; base.
///
/// BC's rule, measured with alc 17.0.34.45391: the test app declaring only the middle app
/// and naming a base-app codeunit fails with <c>error AL0185: Codeunit '...' is missing</c>;
/// with <c>"propagateDependencies": true</c> on the middle app it compiles. The mechanism is
/// <c>ReferenceManager.ResolveDirectReferences</c> in Microsoft.Dynamics.Nav.CodeAnalysis,
/// which adds a declared reference's own dependencies only when they are propagated.
///
/// Each shape the runner compiles source apps through is driven separately, because each
/// pre-pass reaches the compiler with its own reference state: sibling discovery (one bundle
/// argument), the layered pre-pass (three bundle arguments), and a single bundle holding all
/// three apps.
///
/// Spawns the real runner; needs the BC artifact cache. Skips (visibly) when absent.
/// </summary>
public class TransitiveDependencyVisibilityTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private const string BaseCodeunit = "TDV Base Api";

    private sealed record Chain(string Root, string BaseDir, string MiddleDir, string TestDir);

    private static Chain WriteChain(string scratch, bool middlePropagates, bool testReferencesBase)
    {
        Guid baseId = Guid.NewGuid(), middleId = Guid.NewGuid(), testId = Guid.NewGuid();
        var root = Path.Combine(scratch, "chain");
        var baseDir = Path.Combine(root, "base-app");
        var middleDir = Path.Combine(root, "middle-app");
        var testDir = Path.Combine(root, "test-app");
        Directory.CreateDirectory(baseDir);
        Directory.CreateDirectory(middleDir);
        Directory.CreateDirectory(testDir);

        File.WriteAllText(Path.Combine(baseDir, "app.json"), $$"""
        { "id": "{{baseId}}", "name": "TDV Base", "publisher": "AL Runner", "version": "1.0.0.0",
          "dependencies": [], "platform": "1.0.0.0",
          "idRanges": [ { "from": 60050, "to": 60059 } ], "runtime": "14.0" }
        """);
        File.WriteAllText(Path.Combine(baseDir, "BaseApi.Codeunit.al"), """
        codeunit 60051 "TDV Base Api"
        {
            procedure Amplify(Value: Integer): Integer
            begin
                exit((Value * 2) + 3);
            end;
        }
        """);

        File.WriteAllText(Path.Combine(middleDir, "app.json"), $$"""
        { "id": "{{middleId}}", "name": "TDV Middle", "publisher": "AL Runner", "version": "1.0.0.0",
          "dependencies": [ { "id": "{{baseId}}", "name": "TDV Base", "publisher": "AL Runner", "version": "1.0.0.0" } ],
          "propagateDependencies": {{(middlePropagates ? "true" : "false")}},
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 60060, "to": 60069 } ], "runtime": "14.0" }
        """);
        File.WriteAllText(Path.Combine(middleDir, "MiddleApi.Codeunit.al"), """
        codeunit 60060 "TDV Middle Api"
        {
            procedure Relay(Value: Integer): Integer
            var
                BaseApi: Codeunit "TDV Base Api";
            begin
                exit(BaseApi.Amplify(Value) + 100);
            end;
        }
        """);

        File.WriteAllText(Path.Combine(testDir, "app.json"), $$"""
        { "id": "{{testId}}", "name": "TDV Test", "publisher": "AL Runner", "version": "1.0.0.0",
          "dependencies": [ { "id": "{{middleId}}", "name": "TDV Middle", "publisher": "AL Runner", "version": "1.0.0.0" } ],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 60070, "to": 60079 } ], "runtime": "14.0" }
        """);
        // The declared-only body goes through the middle app (11*2+3+100 = 125). The
        // transitive body names the base codeunit directly (11*2+3 = 25).
        var body = testReferencesBase
            ? """
                  var
                      BaseApi: Codeunit "TDV Base Api";
                  begin
                      if BaseApi.Amplify(11) <> 25 then
                          Error('Expected 25 from the base codeunit, got %1', BaseApi.Amplify(11));
                  end;
              """
            : """
                  var
                      MiddleApi: Codeunit "TDV Middle Api";
                  begin
                      if MiddleApi.Relay(11) <> 125 then
                          Error('Expected 125 through the middle codeunit, got %1', MiddleApi.Relay(11));
                  end;
              """;
        File.WriteAllText(Path.Combine(testDir, "VisibilityTests.Codeunit.al"), $$"""
        codeunit 60070 "TDV Tests"
        {
            Subtype = Test;

            [Test]
            procedure ChainValue()
        {{body}}
        }
        """);
        return new Chain(root, baseDir, middleDir, testDir);
    }

    private static (string Output, int Exit) RunRunner(string cacheRoot, params string[] bundles)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        foreach (var b in bundles) args.Append($" \"{b}\"");
        args.Append($" --cache \"{cacheRoot}\"");
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

    private static void AssertRefused(string output, int exit)
    {
        Assert.NotEqual(0, exit);
        Assert.Contains($"AL0185: Codeunit '{BaseCodeunit}' is missing", output);
        Assert.DoesNotContain("PASS  Codeunit60070.ChainValue", output);
    }

    private static void AssertPassed(string output, int exit)
    {
        Assert.DoesNotContain("AL0185", output);
        Assert.True(exit == 0 && output.Contains("PASS  Codeunit60070.ChainValue"),
            $"expected the chain test to compile and pass (exit {exit}):\n{output}");
    }

    // ── Sibling discovery: only the test app is a bundle argument ─────────────

    [SkippableFact]
    public void Sibling_DeclaredDependency_Compiles()
    {
        TestArtifacts.SkipIfMissing();
        var scratch = TestScratch.Dir("tdv-sibling-direct");
        var chain = WriteChain(scratch, middlePropagates: false, testReferencesBase: false);

        var (output, exit) = RunRunner(Path.Combine(scratch, "al-out"), chain.TestDir);

        AssertPassed(output, exit);
    }

    [SkippableFact]
    public void Sibling_UndeclaredTransitiveDependency_RefusedWithAL0185()
    {
        TestArtifacts.SkipIfMissing();
        var scratch = TestScratch.Dir("tdv-sibling-transitive");
        var chain = WriteChain(scratch, middlePropagates: false, testReferencesBase: true);
        var cache = Path.Combine(scratch, "al-out");

        var (output, exit) = RunRunner(cache, chain.TestDir);
        AssertRefused(output, exit);

        // The symbol and AL-output caches sit between the reference set and the
        // observable: a warm run over the same cache root must refuse the same way.
        var (warmOutput, warmExit) = RunRunner(cache, chain.TestDir);
        AssertRefused(warmOutput, warmExit);
    }

    [SkippableFact]
    public void Sibling_PropagatedTransitiveDependency_Compiles()
    {
        TestArtifacts.SkipIfMissing();
        var scratch = TestScratch.Dir("tdv-sibling-propagated");
        var chain = WriteChain(scratch, middlePropagates: true, testReferencesBase: true);
        var cache = Path.Combine(scratch, "al-out");

        var (output, exit) = RunRunner(cache, chain.TestDir);
        AssertPassed(output, exit);

        var (warmOutput, warmExit) = RunRunner(cache, chain.TestDir);
        AssertPassed(warmOutput, warmExit);
    }

    // ── Layered pre-pass: all three apps are bundle arguments ─────────────────

    [SkippableFact]
    public void Layered_UndeclaredTransitiveDependency_RefusedWithAL0185()
    {
        TestArtifacts.SkipIfMissing();
        var scratch = TestScratch.Dir("tdv-layered-transitive");
        var chain = WriteChain(scratch, middlePropagates: false, testReferencesBase: true);

        var (output, exit) = RunRunner(Path.Combine(scratch, "al-out"),
            chain.BaseDir, chain.MiddleDir, chain.TestDir);

        AssertRefused(output, exit);
    }

    [SkippableFact]
    public void Layered_PropagatedTransitiveDependency_Compiles()
    {
        TestArtifacts.SkipIfMissing();
        var scratch = TestScratch.Dir("tdv-layered-propagated");
        var chain = WriteChain(scratch, middlePropagates: true, testReferencesBase: true);

        var (output, exit) = RunRunner(Path.Combine(scratch, "al-out"),
            chain.BaseDir, chain.MiddleDir, chain.TestDir);

        AssertPassed(output, exit);
    }

    // ── One bundle holding all three apps ─────────────────────────────────────

    [SkippableFact]
    public void OneBundle_UndeclaredTransitiveDependency_RefusedWithAL0185()
    {
        TestArtifacts.SkipIfMissing();
        var scratch = TestScratch.Dir("tdv-bundle-transitive");
        var chain = WriteChain(scratch, middlePropagates: false, testReferencesBase: true);

        var (output, exit) = RunRunner(Path.Combine(scratch, "al-out"), chain.Root);

        AssertRefused(output, exit);
    }
}
