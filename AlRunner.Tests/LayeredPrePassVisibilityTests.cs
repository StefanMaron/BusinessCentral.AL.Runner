using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// #2237 — an app compiled from source in one invocation must see the symbols of its declared
/// dependency closure and nothing else. Both source pre-passes, and the per-bundle compile after
/// them, used to hand the compiler every workspace dir written so far, so two apps that do not
/// depend on each other saw each other's symbols. Two observables, each order-dependent before
/// the fix (whichever app compiled second lost):
/// <list type="bullet">
/// <item>two independent apps declaring an object of the same name collided with AL0197;</item>
/// <item>an app referencing an object of an app it does not declare compiled, where BC's
/// compiler reports AL0185 "is missing".</item>
/// </list>
/// Fixtures use no Microsoft objects (platform 1.0.0.0, no declared Microsoft deps) and fresh
/// app ids per test. Spawns the real runner; needs the BC artifact cache.
/// </summary>
public class LayeredPrePassVisibilityTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private sealed record Ids(Guid A, Guid B, Guid Tests);

    private static Ids NewIds() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

    private static void WriteApp(string dir, Guid id, string name, int idFrom, string deps, string fileName, string source)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        {
          "id": "{{id}}",
          "name": "{{name}}",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [ {{deps}} ],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": {{idFrom}}, "to": {{idFrom + 9}} } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(dir, fileName), source);
    }

    private static string Dep(Guid id, string name) =>
        $$"""{ "id": "{{id}}", "name": "{{name}}", "publisher": "AL Runner", "version": "1.0.0.0" }""";

    // Twin A and Twin B both declare codeunit "PPV Shared Name" (different ids) and depend on
    // nothing. The tests app depends on both and calls one codeunit unique to each, so a green
    // run proves both apps compiled AND their code ran.
    private static void WriteTwinA(string dir, Guid id) => WriteApp(dir, id, "PPV Twin A", 60200, "", "TwinA.al", """
        codeunit 60200 "PPV Shared Name"
        {
            procedure Tag(): Text
            begin
                exit('shared-a');
            end;
        }

        codeunit 60201 "PPV Twin A Api"
        {
            procedure Tag(): Text
            begin
                exit('twin-a-60201');
            end;
        }
        """);

    private static void WriteTwinB(string dir, Guid id) => WriteApp(dir, id, "PPV Twin B", 60210, "", "TwinB.al", """
        codeunit 60210 "PPV Shared Name"
        {
            procedure Tag(): Text
            begin
                exit('shared-b');
            end;
        }

        codeunit 60211 "PPV Twin B Api"
        {
            procedure Tag(): Text
            begin
                exit('twin-b-60211');
            end;
        }
        """);

    // Same identity as Twin B, still declaring NO dependency on Twin A, but using Twin A's
    // codeunit. BC's compiler refuses this with AL0185.
    private static void WriteLeakyTwinB(string dir, Guid id) => WriteApp(dir, id, "PPV Twin B", 60210, "", "TwinB.al", """
        codeunit 60211 "PPV Twin B Api"
        {
            procedure Tag(): Text
            var
                TwinA: Codeunit "PPV Twin A Api";
            begin
                exit(TwinA.Tag());
            end;
        }
        """);

    private static void WriteTests(string dir, Ids ids) => WriteApp(dir, ids.Tests, "PPV Twin Tests", 60220,
        Dep(ids.A, "PPV Twin A") + ", " + Dep(ids.B, "PPV Twin B"), "TwinTests.al", """
        codeunit 60220 "PPV Twin Tests"
        {
            Subtype = Test;

            [Test]
            procedure TwinA_CodeRuns()
            var
                TwinA: Codeunit "PPV Twin A Api";
            begin
                if TwinA.Tag() <> 'twin-a-60201' then
                    Error('Expected twin-a-60201, got %1', TwinA.Tag());
            end;

            [Test]
            procedure TwinB_CodeRuns()
            var
                TwinB: Codeunit "PPV Twin B Api";
            begin
                if TwinB.Tag() <> 'twin-b-60211' then
                    Error('Expected twin-b-60211, got %1', TwinB.Tag());
            end;
        }
        """);

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

    private static string NewScratch(string tag) =>
        TestScratch.Dir(Path.Combine("al-runner-layered-visibility", tag));

    private static void AssertBothTwinsCompileAndRun((string Output, int Exit) run, string what)
    {
        Assert.DoesNotContain("AL0197", run.Output);
        Assert.DoesNotContain("COMPILE-FAIL", run.Output);
        Assert.True(run.Exit == 0 && run.Output.Contains("2P/0F/0E"),
            $"{what}: two independent apps sharing an object name must both compile and run (exit {run.Exit}):\n{run.Output}");
    }

    private static void AssertUndeclaredReferenceRefused((string Output, int Exit) run, string what)
    {
        Assert.True(run.Exit != 0,
            $"{what}: an app using an object of an app it does not depend on must not compile (exit {run.Exit}):\n{run.Output}");
        Assert.Contains("AL0185 Codeunit 'PPV Twin A Api' is missing", run.Output);
    }

    // ── The filter itself, no runner spawn ────────────────────────────────────

    [Fact]
    public void WorkspaceDirsInClosure_KeepsOnlyDirsTheResolverPickedAPackageFrom()
    {
        var root = Path.Combine(Path.GetTempPath(), "ppv-closure");
        var dirA = Path.Combine(root, "ws-a");
        var dirB = Path.Combine(root, "ws-b");
        var dirC = Path.Combine(root, "ws-c");
        var manifest = new AppManifest("AL Runner", "A", new Version(1, 0, 0, 0), Guid.NewGuid(), Array.Empty<DependencyRef>());
        var resolved = new List<(AppManifest, string)>
        {
            (manifest, Path.Combine(dirA, "AL_Runner_A_1_0_0_0.app")),
            (manifest, Path.Combine(root, "platform", "Microsoft_System_1_0_0_0.app")),
            (manifest, Path.Combine(dirC, "AL_Runner_C_1_0_0_0.app")),
        };

        var visible = ProgramSupport.WorkspaceDirsInClosure(
            new[] { dirA, dirB, dirC + Path.DirectorySeparatorChar }, resolved);

        Assert.Equal(new[] { dirA, dirC + Path.DirectorySeparatorChar }, visible);
        Assert.Empty(ProgramSupport.WorkspaceDirsInClosure(new[] { dirA, dirB }, new List<(AppManifest, string)>()));
    }

    // ── RunLayeredPrePass: three bundle arguments ─────────────────────────────

    /// <summary>
    /// Both argument orders, each run twice against one cache root: before the fix the app
    /// compiled second failed with AL0197, and a workspace cache HIT on it could hide that.
    /// </summary>
    [SkippableFact]
    public void LayeredPrePass_IndependentAppsSharingAnObjectName_CompileInBothOrders()
    {
        TestArtifacts.SkipIfMissing();

        var scratch = NewScratch("collision");
        var ids = NewIds();
        var a = Path.Combine(scratch, "twin-a");
        var b = Path.Combine(scratch, "twin-b");
        var t = Path.Combine(scratch, "twin-tests");
        WriteTwinA(a, ids.A);
        WriteTwinB(b, ids.B);
        WriteTests(t, ids);
        var cache = Path.Combine(scratch, "cache");

        AssertBothTwinsCompileAndRun(RunRunner(cache, a, b, t), "A,B,T cold");
        AssertBothTwinsCompileAndRun(RunRunner(cache, a, b, t), "A,B,T warm");
        AssertBothTwinsCompileAndRun(RunRunner(cache, b, a, t), "B,A,T");
        AssertBothTwinsCompileAndRun(RunRunner(cache, b, a, t), "B,A,T warm");
    }

    /// <summary>
    /// Before the fix this was order-dependent: with Twin B listed first it failed with AL0185,
    /// with Twin A first it compiled against symbols it never declared.
    /// </summary>
    [SkippableFact]
    public void LayeredPrePass_UndeclaredReferenceToAnotherBundle_RefusedInBothOrders()
    {
        TestArtifacts.SkipIfMissing();

        var scratch = NewScratch("undeclared");
        var ids = NewIds();
        var a = Path.Combine(scratch, "twin-a");
        var b = Path.Combine(scratch, "twin-b");
        var t = Path.Combine(scratch, "twin-tests");
        WriteTwinA(a, ids.A);
        WriteLeakyTwinB(b, ids.B);
        WriteTests(t, ids);
        var cache = Path.Combine(scratch, "cache");

        AssertUndeclaredReferenceRefused(RunRunner(cache, a, b, t), "A,B,T");
        AssertUndeclaredReferenceRefused(RunRunner(cache, a, b, t), "A,B,T warm");
        AssertUndeclaredReferenceRefused(RunRunner(cache, b, a, t), "B,A,T");
    }

    // ── BuildSiblingSourceDeps: one bundle argument, the twins as siblings ────

    [SkippableFact]
    public void SiblingSourceDeps_IndependentSiblingsSharingAnObjectName_Compile()
    {
        TestArtifacts.SkipIfMissing();

        var scratch = NewScratch("sibling-collision");
        var ids = NewIds();
        var apps = Path.Combine(scratch, "apps");
        WriteTwinA(Path.Combine(apps, "twin-a"), ids.A);
        WriteTwinB(Path.Combine(apps, "twin-b"), ids.B);
        var t = Path.Combine(apps, "twin-tests");
        WriteTests(t, ids);
        var cache = Path.Combine(scratch, "cache");

        AssertBothTwinsCompileAndRun(RunRunner(cache, t), "sibling cold");
        AssertBothTwinsCompileAndRun(RunRunner(cache, t), "sibling warm");
    }

    [SkippableFact]
    public void SiblingSourceDeps_UndeclaredReferenceToAnotherSibling_Refused()
    {
        TestArtifacts.SkipIfMissing();

        var scratch = NewScratch("sibling-undeclared");
        var ids = NewIds();
        var apps = Path.Combine(scratch, "apps");
        WriteTwinA(Path.Combine(apps, "twin-a"), ids.A);
        WriteLeakyTwinB(Path.Combine(apps, "twin-b"), ids.B);
        var t = Path.Combine(apps, "twin-tests");
        WriteTests(t, ids);

        AssertUndeclaredReferenceRefused(RunRunner(Path.Combine(scratch, "cache"), t), "sibling");
    }

    // ── --server: the same pre-pass, reached through runTests ─────────────────

    [SkippableFact]
    public async Task ServerRunTests_IndependentAppsSharingAnObjectName_Compile()
    {
        TestArtifacts.SkipIfMissing();

        var scratch = NewScratch("collision-server");
        var ids = NewIds();
        var a = Path.Combine(scratch, "twin-a");
        var b = Path.Combine(scratch, "twin-b");
        var t = Path.Combine(scratch, "twin-tests");
        WriteTwinA(a, ids.A);
        WriteTwinB(b, ids.B);
        WriteTests(t, ids);

        await using var server = await CliServer.StartAsync(new[] { "--cache", Path.Combine(scratch, "cache") });
        var request = JsonSerializer.Serialize(new
        {
            command = "runTests",
            sourcePaths = new[] { b, a, t },
            packagePaths = Array.Empty<string>(),
        });
        var lines = await server.SendRequestStreamingAsync(request, TimeSpan.FromSeconds(300));
        var (_, summary) = ProtocolV2Streaming.Split(lines);

        Assert.False(summary.TryGetProperty("compilationErrors", out _),
            $"unexpected compile errors: {string.Join(" | ", lines)}");
        Assert.Equal(0, summary.GetProperty("exitCode").GetInt32());
        Assert.Equal(2, summary.GetProperty("passed").GetInt32());
    }
}
