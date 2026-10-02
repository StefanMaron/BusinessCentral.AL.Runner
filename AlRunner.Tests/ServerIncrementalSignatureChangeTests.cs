// ServerIncrementalSignatureChangeTests — issues #5093 and #5092, end to end.
//
// #5093: widening a procedure's Code parameter or return value keeps its member id, so on the
// warm --server fast path an UNEDITED caller kept converting to the previous length: a 25-character
// argument still overflowed a parameter widened to Code[30], and a widened return value was stored
// into a Code[20] variable without the overflow check a cold build performs.
// #5092: the server published fast-path output to the AL-output cache under the post-edit key, so a
// fresh process on the same --cache root served whatever the fast path got wrong.
// BcCompilerIncrementalSignatureTests pins the compiler half.
using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

public class ServerIncrementalSignatureChangeTests
{
    private static string AppId(int c) => $"b5093000-000{c}-4a11-9111-111111111111";

    private const string LibBefore = """
        codeunit 71900 "Srv Sig Lib"
        {
            procedure Len(C: Code[20]): Integer
            begin
                exit(StrLen(C));
            end;

            procedure Get(): Code[20]
            begin
                exit('ABC');
            end;
        }
        """;

    private const string Long25 = "ABCDEFGHIJKLMNOPQRSTUVWXY";

    private static readonly string LibWidened = LibBefore
        .Replace("Len(C: Code[20])", "Len(C: Code[30])", StringComparison.Ordinal)
        .Replace("Get(): Code[20]", "Get(): Code[30]", StringComparison.Ordinal)
        .Replace("exit('ABC');", $"exit('{Long25}');", StringComparison.Ordinal);

    /// <summary>A body-only edit: same signatures as <see cref="LibWidened"/>.</summary>
    private static readonly string LibWidenedBodyEdit = LibWidened
        .Replace("exit(StrLen(C));", "exit(StrLen(C) + 0);", StringComparison.Ordinal);

    private static string MakeBundle(string root, int c)
    {
        var dir = Path.Combine(root, "app");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        {
          "id": "{{AppId(c)}}",
          "name": "Srv Sig Change {{c}}",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "idRanges": [ { "from": 71900, "to": 71909 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(dir, "Lib.al"), LibBefore);
        // The call sites live in a codeunit no request ever edits: that is the caller whose C# the
        // fast path reuses.
        File.WriteAllText(Path.Combine(dir, "Probe.al"), """
            codeunit 71901 "Srv Sig Probe"
            {
                procedure LenOf(T: Text): Integer
                var
                    Lib: Codeunit "Srv Sig Lib";
                begin
                    exit(Lib.Len(T));
                end;

                procedure GetIntoShort(): Integer
                var
                    Lib: Codeunit "Srv Sig Lib";
                    C: Code[20];
                begin
                    C := Lib.Get();
                    exit(StrLen(C));
                end;
            }
            """);
        File.WriteAllText(Path.Combine(dir, "Tests.al"), $$"""
            codeunit 71902 "Srv Sig Tests"
            {
                Subtype = Test;

                [Test]
                procedure LongArgumentFits()
                var
                    Probe: Codeunit "Srv Sig Probe";
                begin
                    if Probe.LenOf('{{Long25}}') <> 25 then
                        Error('Len answered %1', Probe.LenOf('{{Long25}}'));
                end;

                [Test]
                procedure LongReturnOverflowsShortVariable()
                var
                    Probe: Codeunit "Srv Sig Probe";
                begin
                    asserterror Probe.GetIntoShort();
                end;
            }
            """);
        return dir;
    }

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

    private static (Dictionary<string, (string Status, string Line)> Tests, JsonElement Summary, string All) Run(List<string> lines)
    {
        var (events, summary) = ProtocolV2Streaming.Split(lines);
        var map = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        foreach (var e in events)
            map[e.GetProperty("name").GetString()!.Split('.').Last()] = (e.GetProperty("status").GetString()!, e.GetRawText());
        return (map, summary, string.Join(" | ", lines));
    }

    private static bool ForcedFull(JsonElement summary)
        => summary.TryGetProperty("selection", out var s) && s.TryGetProperty("forcedFull", out var f) && f.GetBoolean();

    private static int CachedAssemblies(string cacheDir)
        => Directory.Exists(cacheDir) ? Directory.GetFiles(cacheDir, "*.dll", SearchOption.AllDirectories).Length : 0;

    /// <summary>
    /// Both cold-build answers after the widening edit: the 25-character argument fits, and the
    /// 25-character return value overflows the Code[20] variable it is stored into.
    /// </summary>
    private static void ExpectCold(List<string> failures, string stage, Dictionary<string, (string Status, string Line)> tests, string all)
    {
        if (!tests.TryGetValue("LongArgumentFits", out var arg) || arg.Status != "pass")
            failures.Add($"{stage}: LongArgumentFits did not pass, so the caller still converted the argument to the "
                + "previous Code[20] length. Got: " + all);
        if (!tests.TryGetValue("LongReturnOverflowsShortVariable", out var ret) || ret.Status != "pass")
            failures.Add($"{stage}: LongReturnOverflowsShortVariable did not pass, so the caller stored the widened "
                + "return value without the length check a cold build applies. Got: " + all);
    }

    /// <summary>
    /// <paramref name="useCache"/> true adds #5092's half: one <c>--cache</c> root, a check that only
    /// a full compile publishes to it, and a second server process that must answer as a cold build.
    /// Failures are collected per stage so a red names which half broke.
    /// </summary>
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WarmRequest_AfterAProcedureSignatureWidens_AnswersAsAColdRunDoes(bool useCache)
    {
        TestArtifacts.SkipIfMissing();

        var c = useCache ? 2 : 1;
        var root = TestScratch.Dir("al-runner-srv-sig-change");
        Directory.CreateDirectory(root);
        var dir = MakeBundle(root, c);
        var cacheDir = Path.Combine(root, "cache");
        var args = useCache ? new[] { "--cache", cacheDir } : new[] { "--no-cache" };
        var failures = new List<string>();

        await using (var server = await CliServer.StartAsync(args))
        {
            // Request 1: before the edit both tests fail (Code[20] overflows; 'ABC' raises nothing).
            var (before, _, beforeAll) = Run(await server.SendRequestStreamingAsync(RunTestsRequest(dir)));
            Assert.True(before.TryGetValue("LongArgumentFits", out var b1) && b1.Status == "fail", beforeAll);
            Assert.True(before.TryGetValue("LongReturnOverflowsShortVariable", out var b2) && b2.Status == "fail", beforeAll);
            var cachedAfterCold = CachedAssemblies(cacheDir);
            if (useCache) Assert.True(cachedAfterCold > 0, "the cold request published nothing to the cache: " + beforeAll);

            // Request 2: only Lib's signatures change. Touch the tests so affectedOnly selects them
            // whatever it makes of the edit; Probe.al, the caller this is about, stays untouched.
            File.WriteAllText(Path.Combine(dir, "Lib.al"), LibWidened);
            File.AppendAllText(Path.Combine(dir, "Tests.al"), "\n// touched\n");
            var (after, afterSummary, afterAll) = Run(await server.SendRequestStreamingAsync(RunTestsRequest(dir)));
            ExpectCold(failures, "warm request after the signature edit", after, afterAll);
            if (!ForcedFull(afterSummary))
                failures.Add("the signature edit stayed on the incremental fast path (selection.forcedFull false): " + afterAll);
            var cachedAfterFull = CachedAssemblies(cacheDir);

            // Request 3, the control: a body-only edit must stay on the fast path and still answer.
            File.WriteAllText(Path.Combine(dir, "Lib.al"), LibWidenedBodyEdit);
            File.AppendAllText(Path.Combine(dir, "Tests.al"), "\n// touched again\n");
            var (body, bodySummary, bodyAll) = Run(await server.SendRequestStreamingAsync(RunTestsRequest(dir)));
            ExpectCold(failures, "warm request after a body-only edit", body, bodyAll);
            if (ForcedFull(bodySummary))
                failures.Add("a body-only edit fell back to a full compile; the fast path no longer engages: " + bodyAll);

            if (useCache && CachedAssemblies(cacheDir) != cachedAfterFull)
                failures.Add($"the fast-path request published to the AL-output cache ({cachedAfterFull} -> "
                    + $"{CachedAssemblies(cacheDir)} assemblies); only a full compile may (#5092)");
        }

        if (useCache)
        {
            // A fresh process on the same cache root, a plain full run of the post-edit source.
            await using var second = await CliServer.StartAsync(args);
            var (fresh, _, freshAll) = Run(await second.SendRequestStreamingAsync(RunTestsRequest(dir, affectedOnly: false)));
            ExpectCold(failures, "a second server process on the same --cache root", fresh, freshAll);
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }
}
