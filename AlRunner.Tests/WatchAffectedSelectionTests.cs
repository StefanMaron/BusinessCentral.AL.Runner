// WatchAffectedSelectionTests — #5027: `--watch --affected` runs each cycle through the server's
// affectedOnly selection. The server-side proving sequences (ServerAffectedSelection*Tests) are
// driven through a real --watch process here, and every step asserts the exact set of tests the
// cycle ran and that the tests the probed edit breaks fail with the probe.
// Mechanism: docs/watch-affected.md.
using System.Diagnostics;
using System.Text.RegularExpressions;
using Xunit;

namespace AlRunner.Tests;

public class WatchAffectedSelectionTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private static string Helper(string body = "")
        => "codeunit 61770 \"WAff Helper SX\"\n{\n    procedure Value(): Integer\n    begin\n" + body
           + "        exit(1);\n    end;\n}\n";

    private static string Empty(string body = "")
        => "codeunit 61771 \"WAff Empty SX\"\n{\n    procedure P()\n    begin\n" + body + "    end;\n}\n";

    private const string Publisher = """
        codeunit 61772 "WAff Pub SX"
        {
            procedure DoWork()
            begin
                OnDoWork();
            end;

            [IntegrationEvent(false, false)]
            local procedure OnDoWork()
            begin
            end;
        }
        """;

    private static string Table(string triggers = "")
        => "table 61773 \"WAff Tab SX\"\n{\n    fields { field(1; PK; Integer) { } }\n"
           + "    keys { key(PK; PK) { Clustered = true; } }\n" + triggers + "}\n";

    private static string Subscriber(bool bound)
        => "codeunit 61774 \"WAff Sub SX\"\n{\n    procedure Helper(): Integer\n    begin\n        exit(1);\n    end;\n"
           + (bound
               ? "\n    [EventSubscriber(ObjectType::Codeunit, Codeunit::\"WAff Pub SX\", 'OnDoWork', '', false, false)]\n"
                 + "    local procedure Handle()\n    begin\n        Error('PROBE-SUB');\n    end;\n"
               : "")
           + "}\n";

    private const string Tests = """
        codeunit 61775 "WAff Tests SX"
        {
            Subtype = Test;

            [Test]
            procedure UsesHelperOne()
            var
                H: Codeunit "WAff Helper SX";
            begin
                if H.Value() <> 1 then
                    Error('UsesHelperOne failed');
            end;

            [Test]
            procedure UsesHelperTwo()
            var
                H: Codeunit "WAff Helper SX";
            begin
                if H.Value() + 1 <> 2 then
                    Error('UsesHelperTwo failed');
            end;

            [Test]
            procedure CallsEmpty()
            var
                E: Codeunit "WAff Empty SX";
            begin
                E.P();
            end;

            [Test]
            procedure RaisesWork()
            var
                P: Codeunit "WAff Pub SX";
            begin
                P.DoWork();
            end;

            [Test]
            procedure InsertsRow()
            var
                T: Record "WAff Tab SX";
            begin
                T.PK := 1;
                T.Insert(true);
            end;

            [Test]
            procedure Independent()
            begin
                if 2 + 2 <> 4 then
                    Error('Independent failed');
            end;
        }
        """;

    private const string ProbeHelper = "        Error('PROBE-EDIT');\n";
    private const string ProbeEmpty = "        Error('PROBE-EMPTY');\n";
    private const string ProbeInsert = "    trigger OnInsert() begin Error('PROBE-INSERT'); end;\n";

    private static readonly string[] All =
        { "CallsEmpty", "Independent", "InsertsRow", "RaisesWork", "UsesHelperOne", "UsesHelperTwo" };
    private static readonly string[] HelperTests = { "UsesHelperOne", "UsesHelperTwo" };

    private static string Bundle(string prefix, string appIdSuffix)
    {
        var dir = TestScratch.Dir(prefix);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        {
          "id": "c5027000-0000-4a11-9111-{{appIdSuffix}}",
          "name": "Watch Affected SX {{appIdSuffix}}",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 61770, "to": 61789 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(dir, "Helper.Codeunit.al"), Helper());
        File.WriteAllText(Path.Combine(dir, "Empty.Codeunit.al"), Empty());
        File.WriteAllText(Path.Combine(dir, "Pub.Codeunit.al"), Publisher);
        File.WriteAllText(Path.Combine(dir, "Tab.Table.al"), Table());
        File.WriteAllText(Path.Combine(dir, "Sub.Codeunit.al"), Subscriber(bound: false));
        File.WriteAllText(Path.Combine(dir, "Tests.Codeunit.al"), Tests);
        return dir;
    }

    /// <summary>One watch cycle as its stdout printed it.</summary>
    private sealed record Cycle(Dictionary<string, string> Status, Dictionary<string, string> Message,
        List<string> AffectedLines, string Raw)
    {
        public string[] Ran => Status.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();
    }

    private static readonly Regex PassLine = new(@"^PASS\s+Codeunit\d+\.(\w+) \(", RegexOptions.Compiled);
    private static readonly Regex FailLine = new(@"^(FAIL|ERROR)\s+(?:""[^""]*""|Codeunit\d+)\.(\w+) \(", RegexOptions.Compiled);

    private static Cycle Parse(IReadOnlyList<string> stdout)
    {
        var status = new Dictionary<string, string>(StringComparer.Ordinal);
        var message = new Dictionary<string, string>(StringComparer.Ordinal);
        var affected = new List<string>();
        string? last = null;
        foreach (var line in stdout)
        {
            if (PassLine.Match(line) is { Success: true } p) { status[p.Groups[1].Value] = "pass"; last = null; continue; }
            if (FailLine.Match(line) is { Success: true } f)
            {
                last = f.Groups[2].Value;
                status[last] = "fail";
                message[last] = "";
                continue;
            }
            if (last != null && line.StartsWith("      ", StringComparison.Ordinal)) { message[last] += line.Trim() + "\n"; continue; }
            last = null;
            if (line.StartsWith("[watch] affected:", StringComparison.Ordinal)) affected.Add(line);
        }
        return new Cycle(status, message, affected, string.Join("\n", stdout));
    }

    /// <summary>A live `--watch` process, read one cycle at a time off its stdout.</summary>
    private sealed class WatchProcess : IDisposable
    {
        private readonly Process _p;
        private readonly List<CapturedLine> _lines = new();
        private int _cyclesRead;

        public WatchProcess(IEnumerable<string> args)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = TestBuildConfig.RunArgs(ProjectPath) + TestBuildConfig.BcVersionArg + " "
                    + string.Join(" ", args.Select(a => a.StartsWith("--", StringComparison.Ordinal) ? a : $"\"{a}\"")),
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
            };
            _p = Process.Start(psi)!;
            Pump(_p.StandardOutput, OutputStream.Stdout);
            Pump(_p.StandardError, OutputStream.Stderr);
        }

        private void Pump(StreamReader r, OutputStream stream) => Task.Run(async () =>
        {
            string? l;
            while ((l = await r.ReadLineAsync()) != null) lock (_lines) _lines.Add(new CapturedLine(stream, l));
        });

        private string Tail()
        {
            lock (_lines) return string.Join("\n", _lines.TakeLast(60).Select(l => $"[{l.Stream}] {l.Text}"));
        }

        /// <summary>The next cycle's stdout, up to its "waiting for AL source" marker.</summary>
        public async Task<Cycle> NextCycle(TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (true)
            {
                List<string> stdout;
                lock (_lines) stdout = _lines.Where(l => l.Stream == OutputStream.Stdout).Select(l => l.Text).ToList();
                var markers = stdout.Select((t, k) => (t, k))
                    .Where(x => x.t.StartsWith(WatchOutputSlicing.WaitingForSourceMarker, StringComparison.Ordinal))
                    .Select(x => x.k).ToList();
                if (markers.Count > _cyclesRead)
                {
                    var from = _cyclesRead == 0 ? 0 : markers[_cyclesRead - 1] + 1;
                    var to = markers[_cyclesRead];
                    _cyclesRead++;
                    return Parse(stdout.GetRange(from, to - from));
                }
                if (_p.HasExited)
                {
                    await Task.Delay(500);
                    throw new InvalidOperationException($"watch exited (code {_p.ExitCode}) before cycle {_cyclesRead + 1}:\n{Tail()}");
                }
                if (DateTime.UtcNow > deadline)
                    throw new TimeoutException($"cycle {_cyclesRead + 1} did not finish:\n{Tail()}");
                await Task.Delay(200);
            }
        }

        public void Dispose()
        {
            try { _p.Kill(entireProcessTree: true); } catch { }
            _p.WaitForExit(10_000);
            _p.Dispose();
        }
    }

    private static readonly TimeSpan ColdCycle = TimeSpan.FromSeconds(300);
    private static readonly TimeSpan WarmCycle = TimeSpan.FromSeconds(180);

    private static void AssertRan(Cycle c, string step, string[] expected)
        => Assert.True(expected.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(c.Ran),
            $"{step}: ran [{string.Join(", ", c.Ran)}], expected [{string.Join(", ", expected)}]:\n{c.Raw}");

    private static void AssertFailsWith(Cycle c, string step, string test, string probe)
    {
        Assert.True(c.Status.TryGetValue(test, out var s) && s == "fail", $"{step}: {test} must fail:\n{c.Raw}");
        Assert.Contains(probe, c.Message[test], StringComparison.Ordinal);
    }

    private static void AssertAllPass(Cycle c, string step)
        => Assert.True(c.Status.Values.All(s => s == "pass"), $"{step}: every test that ran must pass:\n{c.Raw}");

    /// <summary>The first "[watch] affected:" line: ran N of T, skipped-unaffected U, skipped-failing F.</summary>
    private static void AssertCounts(Cycle c, string step, int ran, int total, int unaffected, int failing)
    {
        Assert.True(c.AffectedLines.Count > 0, $"{step}: no [watch] affected: line:\n{c.Raw}");
        Assert.Equal($"[watch] affected: ran {ran} of {total}   skipped-unaffected {unaffected}   skipped-failing {failing}",
            c.AffectedLines[0]);
    }

    private static void AssertNarrowed(Cycle c, string step)
        => Assert.DoesNotContain(c.AffectedLines, l => l.Contains("full run —", StringComparison.Ordinal));

    /// <summary>
    /// The server's sequences, one watch process on an AL-output cache: edit → revert → re-edit
    /// (#4971, each compile after the first a cache HIT), an empty procedure gaining code (#5011), an
    /// added subscriber (#4988) and an added table trigger (#5008).
    /// </summary>
    [SkippableFact]
    public async Task WatchAffected_ServerSequences_SelectTheTestsTheEditBreaks()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = Bundle("al-runner-watch-affected", "000000000001");
        var cache = TestScratch.Dir("al-runner-watch-affected-cache");
        using var watch = new WatchProcess(new[] { bundle, "--watch", "--affected", "--show-pass", "--cache", cache });

        var first = await watch.NextCycle(ColdCycle);
        AssertRan(first, "cycle 1", All);
        AssertAllPass(first, "cycle 1");
        AssertCounts(first, "cycle 1", 6, 6, 0, 0);
        Assert.Contains(first.AffectedLines, l => l.StartsWith("[watch] affected: full run — ", StringComparison.Ordinal));

        var helper = Path.Combine(bundle, "Helper.Codeunit.al");
        WatchEdit.Replace(helper, Helper(ProbeHelper));
        var edit = await watch.NextCycle(WarmCycle);
        AssertRan(edit, "edit", HelperTests);
        foreach (var t in HelperTests) AssertFailsWith(edit, "edit", t, "PROBE-EDIT");
        AssertCounts(edit, "edit", 2, 6, 4, 0);
        AssertNarrowed(edit, "edit");

        WatchEdit.Replace(helper, Helper());
        var revert = await watch.NextCycle(WarmCycle);
        AssertRan(revert, "revert", HelperTests);
        AssertAllPass(revert, "revert");

        WatchEdit.Replace(helper, Helper(ProbeHelper));
        var reEdit = await watch.NextCycle(WarmCycle);
        AssertRan(reEdit, "re-edit", HelperTests);
        foreach (var t in HelperTests) AssertFailsWith(reEdit, "re-edit", t, "PROBE-EDIT");

        WatchEdit.Replace(helper, Helper());
        var reRevert = await watch.NextCycle(WarmCycle);
        AssertRan(reRevert, "second revert", HelperTests);
        AssertAllPass(reRevert, "second revert");

        WatchEdit.Replace(Path.Combine(bundle, "Empty.Codeunit.al"), Empty(ProbeEmpty));
        var filled = await watch.NextCycle(WarmCycle);
        AssertRan(filled, "empty procedure gains code", new[] { "CallsEmpty" });
        AssertFailsWith(filled, "empty procedure gains code", "CallsEmpty", "PROBE-EMPTY");
        AssertNarrowed(filled, "empty procedure gains code");

        WatchEdit.Replace(Path.Combine(bundle, "Empty.Codeunit.al"), Empty());
        AssertRan(await watch.NextCycle(WarmCycle), "empty procedure emptied", new[] { "CallsEmpty" });

        WatchEdit.Replace(Path.Combine(bundle, "Sub.Codeunit.al"), Subscriber(bound: true));
        var subscribed = await watch.NextCycle(WarmCycle);
        AssertRan(subscribed, "added subscriber", new[] { "RaisesWork" });
        AssertFailsWith(subscribed, "added subscriber", "RaisesWork", "PROBE-SUB");
        AssertNarrowed(subscribed, "added subscriber");

        // RaisesWork is still failing and unaffected by this edit: skipped, counted and named.
        WatchEdit.Replace(Path.Combine(bundle, "Tab.Table.al"), Table(ProbeInsert));
        var trigger = await watch.NextCycle(WarmCycle);
        AssertRan(trigger, "added table trigger", new[] { "InsertsRow" });
        AssertFailsWith(trigger, "added table trigger", "InsertsRow", "PROBE-INSERT");
        AssertCounts(trigger, "added table trigger", 1, 6, 4, 1);
        Assert.Contains(trigger.AffectedLines,
            l => l == "[watch] affected: not re-run, still failing from an earlier cycle: Codeunit61775.RaisesWork");
    }

    /// <summary>
    /// #5007 through watch: a second watch process on the same cache narrows its FIRST cycle from the
    /// baseline the first process persisted; a third with --include-failing reruns the failing ones.
    /// </summary>
    [SkippableFact]
    public async Task WatchAffected_Restart_NarrowsFirstCycleFromPersistedBaseline_AndIncludeFailingReruns()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = Bundle("al-runner-watch-affected-restart", "000000000002");
        var cache = TestScratch.Dir("al-runner-watch-affected-restart-cache");
        string[] Args(params string[] extra)
            => new[] { bundle, "--watch", "--affected", "--show-pass", "--cache", cache }.Concat(extra).ToArray();

        using (var recorder = new WatchProcess(Args()))
        {
            var recorded = await recorder.NextCycle(ColdCycle);
            AssertRan(recorded, "recording process", All);
        }

        File.WriteAllText(Path.Combine(bundle, "Helper.Codeunit.al"), Helper(ProbeHelper));
        using (var restarted = new WatchProcess(Args()))
        {
            var first = await restarted.NextCycle(ColdCycle);
            AssertRan(first, "restarted first cycle", HelperTests);
            foreach (var t in HelperTests) AssertFailsWith(first, "restarted first cycle", t, "PROBE-EDIT");
            AssertCounts(first, "restarted first cycle", 2, 6, 4, 0);
            AssertNarrowed(first, "restarted first cycle");
        }

        File.WriteAllText(Path.Combine(bundle, "Empty.Codeunit.al"), Empty(ProbeEmpty));
        using (var includeFailing = new WatchProcess(Args("--include-failing")))
        {
            var first = await includeFailing.NextCycle(ColdCycle);
            AssertRan(first, "--include-failing first cycle", new[] { "CallsEmpty", "UsesHelperOne", "UsesHelperTwo" });
            AssertFailsWith(first, "--include-failing first cycle", "CallsEmpty", "PROBE-EMPTY");
            foreach (var t in HelperTests) AssertFailsWith(first, "--include-failing first cycle", t, "PROBE-EDIT");
            AssertCounts(first, "--include-failing first cycle", 3, 6, 3, 0);
            AssertNarrowed(first, "--include-failing first cycle");
        }
    }

    /// <summary>Without --affected, --watch is unchanged: every test runs on every cycle.</summary>
    [SkippableFact]
    public async Task Watch_WithoutAffected_RunsEveryTestEveryCycle()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = Bundle("al-runner-watch-unaffected", "000000000003");
        var cache = TestScratch.Dir("al-runner-watch-unaffected-cache");
        using var watch = new WatchProcess(new[] { bundle, "--watch", "--show-pass", "--cache", cache });

        var first = await watch.NextCycle(ColdCycle);
        AssertRan(first, "cycle 1", All);
        Assert.Empty(first.AffectedLines);

        WatchEdit.Replace(Path.Combine(bundle, "Helper.Codeunit.al"), Helper(ProbeHelper));
        var edit = await watch.NextCycle(WarmCycle);
        AssertRan(edit, "edit", All);
        foreach (var t in HelperTests) AssertFailsWith(edit, "edit", t, "PROBE-EDIT");
        Assert.Empty(edit.AffectedLines);
    }
}
