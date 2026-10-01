using System.Text;
using System.Text.Json;

namespace AlRunner.Tests;

/// <summary>
/// #5110: the bundle <see cref="SharedCliServer"/> runs right after startup and again at class
/// end. Each test reads one kind of server-process state a request must not inherit — its own
/// table's rows, a NumberSequence, a SingleInstance codeunit — and fails naming the leak.
/// Rationale and how to read a failure: docs/shared-cli-server.md#the-canary.
/// </summary>
internal static class SharedServerCanary
{
    /// <summary>The three tests, all passing: anything else at the first run is a runner defect.</summary>
    public const int ExpectedTests = 3;

    public static string WriteBundle()
    {
        var dir = TestScratch.Dir("al-runner-shared-server-canary");
        Directory.CreateDirectory(dir);
        // An AppId no fact uses (rule (c) of SharedCliServer). Fixed rather than fresh per
        // fixture so the AL-output cache answers the canary's compile in every class but the first.
        File.WriteAllText(Path.Combine(dir, "app.json"), """
        {
          "id": "5a110c4a-7a2e-4c11-9e5d-c0de5110ca7a",
          "name": "Runner Tests - Shared Server Canary",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "idRanges": [ { "from": 69990, "to": 69992 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(dir, "Canary.al"), """
        table 69990 "ALR Shared Server Canary Row"
        {
            fields
            {
                field(1; "Code"; Code[20]) { }
            }
            keys { key(PK; "Code") { Clustered = true; } }
        }

        codeunit 69991 "ALR Shared Server Canary State"
        {
            SingleInstance = true;

            var
                Hits: Integer;

            procedure Hit(): Integer
            begin
                Hits += 1;
                exit(Hits);
            end;
        }

        codeunit 69992 "ALR Shared Server Canary"
        {
            Subtype = Test;

            [Test]
            procedure CanaryTableStartsEmpty()
            var
                Row: Record "ALR Shared Server Canary Row";
            begin
                if not Row.IsEmpty() then
                    Error('CANARY: %1 row(s) survived from an earlier request', Row.Count());
                Row.Code := 'CANARY';
                Row.Insert();
            end;

            [Test]
            procedure CanaryNumberSequenceStartsAbsent()
            begin
                if NumberSequence.Exists('ALRSharedServerCanary', false) then
                    Error('CANARY: a NumberSequence survived from an earlier request');
                NumberSequence.Insert('ALRSharedServerCanary', 1, 1, false);
            end;

            [Test]
            procedure CanarySingleInstanceStartsFresh()
            var
                State: Codeunit "ALR Shared Server Canary State";
                Hits: Integer;
            begin
                Hits := State.Hit();
                if Hits <> 1 then
                    Error('CANARY: SingleInstance state survived from an earlier request (hit %1)', Hits);
            end;
        }
        """);
        return dir;
    }

    /// <summary>Runs the canary and returns its fingerprint: the summary counts plus every test's name, status and message.</summary>
    public static async Task<string> RunAsync(CliServer server, string bundle)
    {
        var request = JsonSerializer.Serialize(new
        {
            command = "runTests",
            sourcePaths = new[] { bundle },
            packagePaths = Array.Empty<string>(),
        });
        var lines = await server.SendRequestStreamingAsync(request);
        return Fingerprint(lines);
    }

    internal static string Fingerprint(IReadOnlyList<string> lines)
    {
        var sb = new StringBuilder();
        JsonElement? summary = null;
        var tests = new List<string>();
        foreach (var line in lines)
        {
            JsonElement el;
            try { el = JsonSerializer.Deserialize<JsonElement>(line); }
            catch (JsonException) { tests.Add("non-json: " + line); continue; }
            var type = el.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (type == "summary") summary = el;
            else if (type == "test")
                tests.Add($"{Str(el, "name")} | {Str(el, "status")} | {Str(el, "message")}");
            else if (type != "progress")
                tests.Add("unexpected: " + line);
        }
        if (summary is { } s)
            sb.Append("summary total=").Append(Num(s, "total")).Append(" passed=").Append(Num(s, "passed"))
              .Append(" failed=").Append(Num(s, "failed")).Append(" errors=").Append(Num(s, "errors"))
              .Append(" exitCode=").Append(Num(s, "exitCode")).Append('\n');
        else
            sb.Append("no summary line\n");
        foreach (var test in tests.OrderBy(x => x, StringComparer.Ordinal))
            sb.Append(test).Append('\n');
        return sb.ToString();
    }

    /// <summary>The first run must be all three tests passing; a fingerprint of anything else proves nothing at class end.</summary>
    public static void AssertBaseline(string fingerprint)
    {
        var expected = $"summary total={ExpectedTests} passed={ExpectedTests} failed=0 errors=0 exitCode=0\n";
        if (!fingerprint.StartsWith(expected, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "SharedCliServer canary: the first run on a fresh server did not pass all " +
                $"{ExpectedTests} canary tests, so the class-end comparison would prove nothing " +
                "(a startup flag such as --test may be filtering it out).\n" + fingerprint);
    }

    private static string Str(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static string Num(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) ? v.GetRawText() : "-";
}
