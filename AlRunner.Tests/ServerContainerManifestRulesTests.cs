using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// #5107's siblings: compile rules an app.json carries, served through ONE source path that
/// contains two apps. Each fact runs the same container through the CLI and through --server
/// and asserts both give the same answer — <c>internal</c> access across apps without
/// <c>internalsVisibleTo</c> is refused, with it is accepted, and the test app's own
/// <c>preprocessorSymbols</c> reach its <c>#if</c>. Before #5107 the server compiled the
/// container as one module with no app.json, so none of these applied.
/// </summary>
public class ServerContainerManifestRulesTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private static string WriteContainer(string scratch, bool grantInternals)
    {
        Guid libId = Guid.NewGuid(), testId = Guid.NewGuid();
        var root = Path.Combine(scratch, "ws");
        var lib = Path.Combine(root, "lib-app");
        var test = Path.Combine(root, "test-app");
        Directory.CreateDirectory(lib);
        Directory.CreateDirectory(test);
        var grant = grantInternals
            ? $$""", "internalsVisibleTo": [ { "id": "{{testId}}", "name": "SCM Test", "publisher": "AL Runner" } ]"""
            : "";
        File.WriteAllText(Path.Combine(lib, "app.json"), $$"""
        { "id": "{{libId}}", "name": "SCM Lib", "publisher": "AL Runner", "version": "1.0.0.0",
          "dependencies": [], "platform": "1.0.0.0",
          "idRanges": [ { "from": 60080, "to": 60084 } ], "runtime": "14.0"{{grant}} }
        """);
        File.WriteAllText(Path.Combine(lib, "Lib.Codeunit.al"), """
        codeunit 60080 "SCM Lib"
        {
            internal procedure Secret(): Integer
            begin
                exit(42);
            end;
        }
        """);
        File.WriteAllText(Path.Combine(test, "app.json"), $$"""
        { "id": "{{testId}}", "name": "SCM Test", "publisher": "AL Runner", "version": "1.0.0.0",
          "dependencies": [ { "id": "{{libId}}", "name": "SCM Lib", "publisher": "AL Runner", "version": "1.0.0.0" } ],
          "platform": "1.0.0.0", "preprocessorSymbols": [ "SCMSYM" ],
          "idRanges": [ { "from": 60085, "to": 60089 } ], "runtime": "14.0" }
        """);
        File.WriteAllText(Path.Combine(test, "Tests.Codeunit.al"), """
        codeunit 60085 "SCM Tests"
        {
            Subtype = Test;

            [Test]
            procedure SecretViaGrant()
            var
                Lib: Codeunit "SCM Lib";
            begin
                if Lib.Secret() <> 42 then
                    Error('Expected 42, got %1', Lib.Secret());
            end;
        }
        """);
        // A codeunit of its own, so the AL0161 refusal above (which drops its codeunit) leaves it.
        File.WriteAllText(Path.Combine(test, "Symbols.Codeunit.al"), """
        codeunit 60086 "SCM Symbol Tests"
        {
            Subtype = Test;

            [Test]
            procedure OwnPreprocessorSymbol()
            begin
        #if SCMSYM
                exit;
        #else
                Error('SCMSYM from the test app''s own app.json is not defined');
        #endif
            end;
        }
        """);
        return root;
    }

    private static (string Output, int Exit) RunCli(string cacheRoot, string bundle)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        args.Append($" \"{bundle}\" --show-pass --cache \"{cacheRoot}\"");
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

    private static async Task<(int Exit, int Passed, string Text)> Serve(string scratch, string root)
    {
        var args = new List<string> { "--cache", Path.Combine(scratch, "al-out-server") };
        var platformApps = TestArtifacts.PlatformAppsDir();
        if (Directory.Exists(platformApps)) { args.Add("--package-cache"); args.Add(platformApps); }
        await using var server = await CliServer.StartAsync(args);
        var req = JsonSerializer.Serialize(new { command = "runTests", sourcePaths = new[] { root }, packagePaths = Array.Empty<string>() });
        var lines = await server.SendRequestStreamingAsync(req, TimeSpan.FromSeconds(300));
        var (_, summary) = ProtocolV2Streaming.Split(lines);
        var text = string.Join(" | ", lines);
        if (summary.TryGetProperty("compilationErrors", out var groups))
            text += " | " + string.Join(" | ", groups.EnumerateArray()
                .SelectMany(g => g.GetProperty("errors").EnumerateArray().Select(e => e.GetString())));
        return (summary.GetProperty("exitCode").GetInt32(), summary.GetProperty("passed").GetInt32(), text);
    }

    private const string InternalRefusal = "AL0161";

    [SkippableFact]
    public async Task InternalWithoutGrant_RefusedByCliAndServer()
    {
        TestArtifacts.SkipIfMissing();
        var scratch = TestScratch.Dir("scm-no-grant");
        var root = WriteContainer(scratch, grantInternals: false);

        var (cli, cliExit) = RunCli(Path.Combine(scratch, "al-out-cli"), root);
        Assert.True(cliExit != 0, cli);
        Assert.True(cli.Contains(InternalRefusal), cli);
        Assert.True(cli.Contains("PASS  Codeunit60086.OwnPreprocessorSymbol"), cli);

        var (exit, _, text) = await Serve(scratch, root);
        // The server runs none of a module's tests once an object is dropped, where the CLI runs
        // the surviving codeunit (#5118), so only the refusal is compared here.
        Assert.True(exit != 0, text);
        Assert.True(text.Contains(InternalRefusal), text);
    }

    [SkippableFact]
    public async Task InternalWithGrant_AcceptedByCliAndServer()
    {
        TestArtifacts.SkipIfMissing();
        var scratch = TestScratch.Dir("scm-grant");
        var root = WriteContainer(scratch, grantInternals: true);

        var (cli, cliExit) = RunCli(Path.Combine(scratch, "al-out-cli"), root);
        Assert.True(cliExit == 0, cli);
        Assert.True(cli.Contains("PASS  Codeunit60085.SecretViaGrant")
            && cli.Contains("PASS  Codeunit60086.OwnPreprocessorSymbol"), cli);

        var (exit, passed, text) = await Serve(scratch, root);
        Assert.True(exit == 0 && passed == 2, text);
    }
}
