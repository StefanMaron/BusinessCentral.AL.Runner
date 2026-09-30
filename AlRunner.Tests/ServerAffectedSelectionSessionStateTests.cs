// ServerAffectedSelectionSessionStateTests — #5050: WorkDate, number sequences and SingleInstance
// codeunits keep their state across every test boundary and isolation, so a test that reads state an
// earlier test wrote is selected when the writer's code changes. Mechanism:
// docs/server-mode.md#affectedonly-and-session-state.
using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

public class ServerAffectedSelectionSessionStateTests
{
    // The #5048 reviewer's reproducer: A sets WorkDate and draws from a sequence through a helper;
    // B and C read what A left under per-test isolation; D reads no session state.
    internal static string SessionHelper(string date = "20200101D", int nexts = 1)
        => """
        codeunit 61900 "SS Helper"
        {
            procedure SetUp()
            var
                I: Integer;
                V: BigInteger;
            begin
        """ + $"        WorkDate({date});" + """

                if not NumberSequence.Exists('SS5050SEQ') then
                    NumberSequence.Insert('SS5050SEQ', 1, 1);
        """ + $"        for I := 1 to {nexts} do" + """

                    V := NumberSequence.Next('SS5050SEQ');
            end;
        }
        """;

    private const string SessionTests = """
        codeunit 61905 "SS Tests"
        {
            Subtype = Test;

            [Test]
            procedure A_SetsState()
            var
                H: Codeunit "SS Helper";
            begin
                H.SetUp();
            end;

            [Test]
            procedure B_ReadsWorkDate()
            begin
                if WorkDate() <> 20200101D then
                    Error('WORKDATE-%1', WorkDate());
            end;

            [Test]
            procedure C_ReadsSequence()
            begin
                if NumberSequence.Current('SS5050SEQ') <> 1 then
                    Error('SEQ-%1', NumberSequence.Current('SS5050SEQ'));
            end;

            [Test]
            procedure D_ReadsNothing()
            begin
                if 2 + 2 <> 4 then
                    Error('D failed');
            end;
        }
        """;

    // The issue's shape: a SingleInstance store written from one test codeunit, read from another.
    // One object per file: a file declaring several maps its statements to no single object (#5003).
    internal static string Store(string put = "Stored := V;") => """
        codeunit 61910 "SS Store"
        {
            SingleInstance = true;

            var
                Stored: Integer;

            procedure Put(V: Integer)
            begin
        """ + "        " + put + """

            end;

            procedure Get(): Integer
            begin
                exit(Stored);
            end;
        }
        """;

    private const string WriterTests = """
        codeunit 61911 "SS Writer Tests"
        {
            Subtype = Test;

            [Test]
            procedure Writes()
            var
                S: Codeunit "SS Store";
            begin
                S.Put(42);
            end;
        }
        """;

    private const string ReaderTests = """
        codeunit 61912 "SS Reader Tests"
        {
            Subtype = Test;

            [Test]
            procedure Reads()
            var
                S: Codeunit "SS Store";
            begin
                if S.Get() <> 42 then
                    Error('READS-%1', S.Get());
            end;
        }
        """;

    private const string ControlTests = """
        codeunit 61913 "SS Control Tests"
        {
            Subtype = Test;

            [Test]
            procedure Independent()
            begin
                if 3 + 3 <> 6 then
                    Error('Independent failed');
            end;
        }
        """;

    private static string Bundle(string prefix, string appIdSuffix, params (string File, string Content)[] files)
    {
        var dir = TestScratch.Dir(prefix);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        {
          "id": "c5050000-0000-4a11-9111-{{appIdSuffix}}",
          "name": "Session State SS {{appIdSuffix}}",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 61900, "to": 61919 } ],
          "runtime": "14.0"
        }
        """);
        foreach (var (file, content) in files) File.WriteAllText(Path.Combine(dir, file), content);
        return dir;
    }

    private static string SessionBundle(string prefix, string suffix) => Bundle(prefix, suffix,
        ("Helper.Codeunit.al", SessionHelper()), ("Tests.Codeunit.al", SessionTests));

    internal static string StoreBundle(string prefix, string suffix) => Bundle(prefix, suffix,
        ("Store.Codeunit.al", Store()), ("Writer.Codeunit.al", WriterTests), ("Reader.Codeunit.al", ReaderTests),
        ("Control.Codeunit.al", ControlTests));

    private sealed record Observed(Dictionary<string, (string Status, string Line)> Tests, bool ForcedFull, string Raw)
    {
        public string[] Ran => Tests.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();
    }

    private static async Task<Observed> Send(CliServer server, string bundle, string? isolation, bool affectedOnly = true)
    {
        var request = new Dictionary<string, object>
        {
            ["command"] = "runTests",
            ["sourcePaths"] = new[] { bundle },
            ["packagePaths"] = Array.Empty<string>(),
            ["affectedOnly"] = affectedOnly,
        };
        if (isolation != null) request["testIsolation"] = isolation;
        var lines = await server.SendRequestStreamingAsync(JsonSerializer.Serialize(request), TimeSpan.FromSeconds(180));
        var (events, summary) = ProtocolV2Streaming.Split(lines);
        var raw = string.Join(" | ", lines) + "\n--- stderr ---\n" + server.StdErr;
        var forced = summary.TryGetProperty("selection", out var selection) && selection.GetProperty("forcedFull").GetBoolean();
        var tests = events.ToDictionary(
            e => e.GetProperty("name").GetString()!.Split('.').Last(),
            e => (e.GetProperty("status").GetString()!, e.GetRawText()), StringComparer.Ordinal);
        return new Observed(tests, forced, raw);
    }

    private static void AssertRan(Observed o, string step, params string[] expected)
        => Assert.True(expected.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(o.Ran),
            $"{step}: ran [{string.Join(", ", o.Ran)}], expected [{string.Join(", ", expected)}]:\n{o.Raw}");

    private static void AssertFailsWith(Observed o, string test, string text)
    {
        Assert.True(o.Tests.TryGetValue(test, out var t) && t.Status == "fail", $"{test} must fail:\n{o.Raw}");
        Assert.Contains(text, t.Line, StringComparison.Ordinal);
    }

    /// <summary>
    /// The reviewer's reproducer under Test isolation: editing the helper that sets WorkDate and
    /// draws from the sequence selects both readers, which fail exactly as a full run fails them.
    /// The test reading no session state stays skipped.
    /// </summary>
    [SkippableFact]
    public async Task TestIsolation_WorkDateAndSequenceReaders_AreSelectedWhenTheirWriterChanges()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = SessionBundle("al-runner-server-affected-session-state", "000000000001");
        await using var server = await CliServer.StartAsync(new[] { "--no-cache" });

        var baseline = await Send(server, bundle, "test");
        Assert.True(baseline.ForcedFull, baseline.Raw);
        Assert.True(baseline.Tests.Values.All(t => t.Status == "pass"), baseline.Raw);
        AssertRan(await Send(server, bundle, "test"), "unchanged");

        File.WriteAllText(Path.Combine(bundle, "Helper.Codeunit.al"), SessionHelper("20210101D", 2));
        var edited = await Send(server, bundle, "test");
        Assert.False(edited.ForcedFull, edited.Raw);
        AssertRan(edited, "edit", "A_SetsState", "B_ReadsWorkDate", "C_ReadsSequence");
        AssertFailsWith(edited, "B_ReadsWorkDate", "WORKDATE-01/01/21");
        AssertFailsWith(edited, "C_ReadsSequence", "SEQ-2");

        // The full run agrees on every test the selection ran, and D was right to be skipped.
        var full = await Send(server, bundle, "test", affectedOnly: false);
        AssertFailsWith(full, "B_ReadsWorkDate", "WORKDATE-01/01/21");
        AssertFailsWith(full, "C_ReadsSequence", "SEQ-2");
        Assert.Equal("pass", full.Tests["D_ReadsNothing"].Status);
    }

    /// <summary>
    /// The issue's shape under the default Codeunit isolation: a procedure-scope edit to the
    /// SingleInstance store's writer selects the test in another codeunit that reads it.
    /// </summary>
    [SkippableFact]
    public async Task CodeunitIsolation_SingleInstanceReaderInAnotherCodeunit_IsSelectedWhenTheWriterChanges()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = StoreBundle("al-runner-server-affected-session-si", "000000000002");
        await using var server = await CliServer.StartAsync(new[] { "--no-cache" });

        var baseline = await Send(server, bundle, null);
        Assert.True(baseline.ForcedFull, baseline.Raw);
        Assert.True(baseline.Tests.Values.All(t => t.Status == "pass"), baseline.Raw);

        File.WriteAllText(Path.Combine(bundle, "Store.Codeunit.al"), Store("Stored := V + 1;"));
        var edited = await Send(server, bundle, null);
        Assert.False(edited.ForcedFull, edited.Raw);
        AssertRan(edited, "edit", "Reads", "Writes");
        AssertFailsWith(edited, "Reads", "READS-43");
    }

    /// <summary>
    /// The other direction: an edit to a reader alone brings the test that writes what it reads,
    /// so the reader sees the state a full run gives it and passes. Without the writer it would
    /// find no sequence and fail where a full run passes.
    /// </summary>
    [SkippableFact]
    public async Task TestIsolation_ASelectedReader_BringsTheWriterItReadsFrom()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = SessionBundle("al-runner-server-affected-session-provenance", "000000000004");
        await using var server = await CliServer.StartAsync(new[] { "--no-cache" });

        Assert.True((await Send(server, bundle, "test")).ForcedFull);

        File.WriteAllText(Path.Combine(bundle, "Tests.Codeunit.al"),
            SessionTests.Replace("'SEQ-%1'", "'SEQUENCE-%1'", StringComparison.Ordinal));
        var edited = await Send(server, bundle, "test");
        Assert.False(edited.ForcedFull, edited.Raw);
        AssertRan(edited, "reader edit", "A_SetsState", "C_ReadsSequence");
        Assert.True(edited.Tests.Values.All(t => t.Status == "pass"), edited.Raw);
    }

    /// <summary>The same through the persisted baseline: a restarted server selects the reader first time.</summary>
    [SkippableFact]
    public async Task AcrossARestart_TheSessionStateReadersAreSelected()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = SessionBundle("al-runner-server-affected-session-restart", "000000000003");
        var cache = TestScratch.Dir("al-runner-server-affected-session-restart-cache");

        await using (var recorder = await CliServer.StartAsync(new[] { "--cache", cache }))
            Assert.True((await Send(recorder, bundle, "test")).ForcedFull);

        File.WriteAllText(Path.Combine(bundle, "Helper.Codeunit.al"), SessionHelper("20210101D", 2));
        await using var restarted = await CliServer.StartAsync(new[] { "--cache", cache });
        var edited = await Send(restarted, bundle, "test");
        Assert.False(edited.ForcedFull, $"the persisted baseline must let the first request narrow: {edited.Raw}");
        AssertRan(edited, "restarted", "A_SetsState", "B_ReadsWorkDate", "C_ReadsSequence");
        AssertFailsWith(edited, "B_ReadsWorkDate", "WORKDATE-01/01/21");
        AssertFailsWith(edited, "C_ReadsSequence", "SEQ-2");
    }
}
