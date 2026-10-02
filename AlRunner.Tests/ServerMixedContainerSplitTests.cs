using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// #5119: a --server source path holding both app.json suites and manifest-less suites must
/// compile as the CLI's AppGroups do: each app.json suite as its own module, the manifest-less
/// suites merged into one fallback module. The oracle is a CLI run of the same container; the
/// facts compare which tests ran and how each ended. The marker is <c>#if APPSYM</c>: defined by
/// the app suites' own <c>preprocessorSymbols</c> and, in the CLI, by nothing else.
/// </summary>
public class ServerMixedContainerSplitTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    // Passes only when APPSYM is defined, i.e. when its module compiles under the app.json that
    // declares it.
    private static string NeedsSymbol(int id, string name) => $$"""
        codeunit {{id}} "{{name}}"
        {
            Subtype = Test;

            [Test]
            procedure Own()
            begin
        #if APPSYM
                exit;
        #else
                Error('APPSYM is not defined here');
        #endif
            end;
        }
        """;

    // The reverse: passes only when APPSYM is NOT defined (a manifest-less suite has no manifest).
    private static string ForbidsSymbol(int id, string name) => $$"""
        codeunit {{id}} "{{name}}"
        {
            Subtype = Test;

            [Test]
            procedure Own()
            begin
        #if APPSYM
                Error('APPSYM leaked into a manifest-less suite');
        #else
                exit;
        #endif
            end;
        }
        """;

    private static void App(string root, string dir, int from, string name)
    {
        Write(Path.Combine(root, dir, "app.json"), $$"""
        { "id": "{{Guid.NewGuid()}}", "name": "{{name}}", "publisher": "AL Runner", "version": "1.0.0.0",
          "dependencies": [], "preprocessorSymbols": [ "APPSYM" ],
          "idRanges": [ { "from": {{from}}, "to": {{from + 4}} } ], "runtime": "14.0" }
        """);
        Write(Path.Combine(root, dir, "Tests.Codeunit.al"), NeedsSymbol(from, name + " Tests"));
    }

    private static void Split(string root, string dir, int id, string name)
        => Write(Path.Combine(root, dir, "test", "Tests.Codeunit.al"), ForbidsSymbol(id, name + " Tests"));

    private static string WriteContainer(string scratch, string shape)
    {
        var root = Path.Combine(scratch, "ws");
        switch (shape)
        {
            case "mixed":
                App(root, "app-one", 60100, "MCS One");
                Split(root, "split-suite", 60110, "MCS Split");
                App(root, "app-two", 60120, "MCS Two");
                break;
            case "apps-only":
                App(root, "app-one", 60100, "MCS One");
                App(root, "app-two", 60120, "MCS Two");
                break;
            case "manifestless-only":
                Split(root, "split-one", 60110, "MCS Split One");
                Split(root, "split-two", 60130, "MCS Split Two");
                break;
            default: throw new ArgumentException(shape);
        }
        return root;
    }

    private static Dictionary<string, string> RunCli(string cacheRoot, string bundle)
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
        string text;
        lock (sb) text = sb.ToString();
        var results = new Dictionary<string, string>();
        foreach (Match m in Regex.Matches(text, @"^\s*(PASS|FAIL)\s+(Codeunit\d+\.\w+)", RegexOptions.Multiline))
            results[m.Groups[2].Value] = m.Groups[1].Value;
        Assert.True(results.Count > 0, "the CLI reported no test results:\n" + text);
        return results;
    }

    private static async Task<Dictionary<string, string>> Serve(string cacheRoot, string root)
    {
        var args = new List<string> { "--cache", cacheRoot };
        var platformApps = TestArtifacts.PlatformAppsDir();
        if (Directory.Exists(platformApps)) { args.Add("--package-cache"); args.Add(platformApps); }
        await using var server = await CliServer.StartAsync(args);
        var req = JsonSerializer.Serialize(new { command = "runTests", sourcePaths = new[] { root }, packagePaths = Array.Empty<string>() });
        var lines = await server.SendRequestStreamingAsync(req, TimeSpan.FromSeconds(300));
        var (events, _) = ProtocolV2Streaming.Split(lines);
        return events.ToDictionary(
            e => e.GetProperty("name").GetString()!,
            e => e.GetProperty("status").GetString() == "pass" ? "PASS" : "FAIL");
    }

    /// <summary>The server is run twice against one cache root: the second request reads the
    /// AL-output cache the first populated, and must give the CLI's answer as well.</summary>
    [SkippableTheory]
    [InlineData("mixed")]
    [InlineData("apps-only")]
    [InlineData("manifestless-only")]
    public async Task ServerRunsTheContainerAsTheCliDoes(string shape)
    {
        TestArtifacts.SkipIfMissing();
        var scratch = TestScratch.Dir("mcs-" + shape);
        var root = WriteContainer(scratch, shape);

        var cli = RunCli(Path.Combine(scratch, "al-out-cli"), root);
        Assert.All(cli, kv => Assert.Equal("PASS", kv.Value));

        var serverCache = Path.Combine(scratch, "al-out-server");
        var cold = await Serve(serverCache, root);
        var warm = await Serve(serverCache, root);

        Assert.Equal(cli.OrderBy(kv => kv.Key), cold.OrderBy(kv => kv.Key));
        Assert.Equal(cli.OrderBy(kv => kv.Key), warm.OrderBy(kv => kv.Key));
    }
}
