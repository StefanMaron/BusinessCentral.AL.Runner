// ServerIncrementalInterfaceExtendsTests — issue #5089, end to end.
//
// A warm --server request must answer as a cold run of the same sources does after an interface
// gains an `extends` clause. Before the fix the incremental compile re-emitted only the interface,
// the implementing codeunit kept its old interface list, and `G is "<extended interface>"` kept
// answering false: a test a cold compile fails kept passing, until the server restarted.
// BcCompilerIncrementalInterfaceDependentsTests pins the mechanism at the compiler.
using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

public class ServerIncrementalInterfaceExtendsTests
{
    private static string AppId(int c) => $"b5089000-000{c}-4a11-9111-111111111111";

    private const string GreeterBefore = """
        interface "Srv Iface Greeter"
        {
            procedure Greet(): Text;
        }
        """;

    private const string GreeterAfter = """
        interface "Srv Iface Greeter" extends "Srv Iface Other"
        {
            procedure Greet(): Text;
        }
        """;

    private static string MakeBundle(string root, int c)
    {
        var dir = Path.Combine(root, "app");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        {
          "id": "{{AppId(c)}}",
          "name": "Srv Iface Extends {{c}}",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 71890, "to": 71899 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(dir, "Greeter.al"), GreeterBefore);
        File.WriteAllText(Path.Combine(dir, "Other.al"), """
            interface "Srv Iface Other"
            {
                procedure Hello(): Text;
            }
            """);
        File.WriteAllText(Path.Combine(dir, "Impl.al"), """
            codeunit 71890 "Srv Iface Impl" implements "Srv Iface Greeter"
            {
                procedure Hello(): Text
                begin
                    exit('hi');
                end;

                procedure Greet(): Text
                begin
                    exit('g');
                end;
            }
            """);
        // The interface-typed code lives in a helper the edit never touches: a codeunit that binds
        // `G := Impl` in the same delta as the interface edit makes BC reject the stale Impl symbol
        // and fall back on its own, which would hide the defect.
        File.WriteAllText(Path.Combine(dir, "Probe.al"), """
            codeunit 71892 "Srv Iface Probe"
            {
                procedure ImplIsOther(): Boolean
                var
                    Impl: Codeunit "Srv Iface Impl";
                    G: Interface "Srv Iface Greeter";
                begin
                    G := Impl;
                    exit(G is "Srv Iface Other");
                end;

                procedure Greet(): Text
                var
                    Impl: Codeunit "Srv Iface Impl";
                    G: Interface "Srv Iface Greeter";
                begin
                    G := Impl;
                    exit(G.Greet());
                end;
            }
            """);
        File.WriteAllText(Path.Combine(dir, "Tests.al"), """
            codeunit 71891 "Srv Iface Tests"
            {
                Subtype = Test;

                [Test]
                procedure ImplIsNotOther()
                var
                    Probe: Codeunit "Srv Iface Probe";
                begin
                    if Probe.ImplIsOther() then
                        Error('G is Other');
                end;

                [Test]
                procedure GreetStillAnswers()
                var
                    Probe: Codeunit "Srv Iface Probe";
                begin
                    if Probe.Greet() <> 'g' then
                        Error('Greet answered %1', Probe.Greet());
                end;
            }
            """);
        return dir;
    }

    // affectedOnly + perTestCoverage is what puts a request on the incremental change model.
    private static string RunTestsRequest(string dir, bool affectedOnly = true)
        => affectedOnly
            ? JsonSerializer.Serialize(new
            {
                command = "runTests",
                sourcePaths = new[] { dir },
                packagePaths = Array.Empty<string>(),
                affectedOnly = true,
                perTestCoverage = true,
            })
            : JsonSerializer.Serialize(new
            {
                command = "runTests",
                sourcePaths = new[] { dir },
                packagePaths = Array.Empty<string>(),
            });

    private static Dictionary<string, (string Status, string Line)> ByTest(List<string> lines)
    {
        var (events, _) = ProtocolV2Streaming.Split(lines);
        var map = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        foreach (var e in events)
            map[e.GetProperty("name").GetString()!.Split('.').Last()] = (e.GetProperty("status").GetString()!, e.GetRawText());
        return map;
    }

    /// <summary>
    /// <paramref name="useCache"/> false: <c>--no-cache</c>, the issue's configuration.
    /// True: one <c>--cache</c> root, and after the warm request a SECOND server process asks the
    /// same post-edit question, so a stale output written to the AL-output cache by the warm
    /// request would be served back as a HIT.
    /// </summary>
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WarmRequest_AfterAnInterfaceGainsAnExtendsClause_AnswersAsAColdRunDoes(bool useCache)
    {
        TestArtifacts.SkipIfMissing();

        var c = useCache ? 2 : 1;
        var root = TestScratch.Dir("al-runner-srv-iface-extends");
        Directory.CreateDirectory(root);
        var dir = MakeBundle(root, c);
        var cacheDir = Path.Combine(root, "cache");
        var args = useCache ? new[] { "--cache", cacheDir } : new[] { "--no-cache" };

        await using (var server = await CliServer.StartAsync(args))
        {
            // Request 1: Greeter does not extend Other, so the implementer is not an Other.
            var before = ByTest(await server.SendRequestStreamingAsync(RunTestsRequest(dir)));
            Assert.Equal("pass", before["ImplIsNotOther"].Status);
            Assert.Equal("pass", before["GreetStillAnswers"].Status);

            File.WriteAllText(Path.Combine(dir, "Greeter.al"), GreeterAfter);
            // Touch the test codeunit too, so affectedOnly selects its tests whatever it makes of an
            // interface edit (#5083). Impl.al, the file the defect is about, stays untouched.
            File.AppendAllText(Path.Combine(dir, "Tests.al"), "\n// touched\n");

            // Request 2, same process: a cold compile of these sources makes Impl an Other.
            var after = ByTest(await server.SendRequestStreamingAsync(RunTestsRequest(dir)));
            Assert.True(after.TryGetValue("ImplIsNotOther", out var warm) && warm.Status == "fail",
                "the warm server still answered as though Greeter did not extend Other: the implementer's "
                + "C# was reused with its previous interface list. Got: "
                + string.Join(" | ", after.Values.Select(v => v.Line)));
            Assert.Contains("G is Other", warm.Line, StringComparison.Ordinal);
            Assert.Equal("pass", after["GreetStillAnswers"].Status);
        }

        if (!useCache) return;

        // A fresh process on the same cache root: whatever the warm request left under this source's
        // key must answer the same way. A plain full run: affectedOnly would select nothing, since
        // the persisted affectedOnly baseline already describes this source.
        await using var second = await CliServer.StartAsync(args);
        var cold = ByTest(await second.SendRequestStreamingAsync(RunTestsRequest(dir, affectedOnly: false)));
        Assert.True(cold.TryGetValue("ImplIsNotOther", out var hit) && hit.Status == "fail",
            "a fresh server on the same --cache root served a stale implementer for the post-edit source. Got: "
            + string.Join(" | ", cold.Values.Select(v => v.Line)));
        Assert.Contains("G is Other", hit.Line, StringComparison.Ordinal);
    }
}
