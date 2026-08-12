using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Proves the layered-build workspace cache is keyed PER dependency package, so
/// editing one impl does not rebuild the unchanged siblings.
///
/// Setup: app C depends on impls A and B (both source-only, so the layered
/// pre-pass builds them into the workspace cache). After a clean first run we edit
/// ONLY A and re-run: B must report a `[layered] cache HIT` (untouched), while A
/// re-emits. With a combined workspace key, editing A orphans B's cache too and B
/// re-emits — which this test fails on.
///
/// Spawns the real runner; needs the BC artifact cache. Skips (no-op) when absent.
/// </summary>
[Collection("server-serial")]
public class LayeredCacheTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private static string CurrentFramework()
    {
        var v = Environment.Version;
        return $"net{v.Major}.{v.Minor}";
    }

    private static void WriteApp(string dir, string id, string name, int idFrom,
        string codeunit, string? dependsOnJson = null)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        {
          "id": "{{id}}",
          "name": "{{name}}",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [{{dependsOnJson ?? ""}}],
          "platform": "1.0.0.0",
          "application": "1.0.0.0",
          "idRanges": [ { "from": {{idFrom}}, "to": {{idFrom + 9}} } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(dir, "Probe.Codeunit.al"), codeunit);
    }

    private static (string output, int exit) RunRunner(string cacheDir, params string[] bundles)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        foreach (var b in bundles) args.Append(" \"").Append(b).Append('"');
        args.Append(" --cache \"").Append(cacheDir).Append('"');
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
    public void EditingOneImpl_DoesNotRebuildSiblingImpl()
    {
        TestArtifacts.SkipIfMissing();

        var root = Path.Combine(Path.GetTempPath(), "al-runner-layered-cache", Guid.NewGuid().ToString("N"));
        var cacheDir = Path.Combine(root, "al-out");
        var aDir = Path.Combine(root, "A");
        var bDir = Path.Combine(root, "B");
        var cDir = Path.Combine(root, "C");
        // Unique tokens so the first run is always a fresh WROTE, never a HIT from a
        // previous test invocation (the workspace cache is keyed on file content).
        var tokA = Guid.NewGuid().ToString("N");
        var tokB = Guid.NewGuid().ToString("N");
        var idA = "c1000000-0000-4000-8000-000000000a01";
        var idB = "c1000000-0000-4000-8000-000000000b01";
        var idC = "c1000000-0000-4000-8000-000000000c01";

        WriteApp(aDir, idA, "Layered A LC", 60130,
            $"codeunit 60130 \"Layered A Cu LC\"\n{{\n    // marker {tokA}\n    procedure A_Ping(): Integer begin exit(1); end;\n}}\n");
        WriteApp(bDir, idB, "Layered B LC", 60135,
            $"codeunit 60135 \"Layered B Cu LC\"\n{{\n    // marker {tokB}\n    procedure B_Ping(): Integer begin exit(2); end;\n}}\n");
        var depsJson =
            $"{{ \"id\": \"{idA}\", \"name\": \"Layered A LC\", \"publisher\": \"AL Runner\", \"version\": \"1.0.0.0\" }}," +
            $"{{ \"id\": \"{idB}\", \"name\": \"Layered B LC\", \"publisher\": \"AL Runner\", \"version\": \"1.0.0.0\" }}";
        WriteApp(cDir, idC, "Layered C LC", 60140,
            "codeunit 60140 \"Layered C Cu LC\"\n{\n    Subtype = Test;\n    [Test] procedure Trivial() begin end;\n}\n",
            dependsOnJson: depsJson);

        // Run 1 — both impls fresh-built.
        var (out1, _) = RunRunner(cacheDir, aDir, bDir, cDir);
        Assert.Contains("[layered] WROTE Layered A LC", out1);
        Assert.Contains("[layered] WROTE Layered B LC", out1);

        // Edit ONLY A.
        File.WriteAllText(Path.Combine(aDir, "Probe.Codeunit.al"),
            $"codeunit 60130 \"Layered A Cu LC\"\n{{\n    // marker {tokA}-EDITED\n    procedure A_Ping(): Integer begin exit(99); end;\n}}\n");

        // Run 2 — A re-emits, B must be a cache HIT (the fix); a combined key would
        // rebuild B too.
        var (out2, _) = RunRunner(cacheDir, aDir, bDir, cDir);
        Assert.Contains("[layered] WROTE Layered A LC", out2);
        Assert.Contains("[layered] cache HIT Layered B LC", out2);
        Assert.DoesNotContain("[layered] WROTE Layered B LC", out2);
    }
}
