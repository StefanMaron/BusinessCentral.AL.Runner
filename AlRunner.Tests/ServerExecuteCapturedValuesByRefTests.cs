using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Issue #2488 — end-to-end proof, on the wire, that a `var` (by-reference) AL parameter
/// reports its VALUE under `--capture-values`, not the CLR type name of the
/// `Microsoft.Dynamics.Nav.Runtime.ByRef&lt;T&gt;` wrapper BC materialises it as.
///
/// The mechanism tests in AlValueWireFormatByRefTests construct a `ByRef&lt;T&gt;` by hand;
/// this one compiles and runs the reporter's own reproducer through BC's real emitter, so
/// it also pins the premise — that a `var` parameter really does land on the generated
/// `*_Scope` class as a wrapper — rather than assuming it.
///
/// Ghost-test guard: the assertions name concrete values (5 then 6 for the integer, "y"
/// for the text) AND the JSON kind they must arrive as. A renderer that stringified
/// everything fails on <c>ValueKind.Number</c>; one that emitted the wrapper name fails on
/// the values; one that unwrapped nothing fails both. The by-value control in the same
/// response (`total`, `name` in OnRun) fails if a fix unwrapped indiscriminately.
/// </summary>
public class ServerExecuteCapturedValuesByRefTests : IClassFixture<SharedCliServer>
{
    private readonly SharedCliServer _fixture;

    public ServerExecuteCapturedValuesByRefTests(SharedCliServer fixture) => _fixture = fixture;

    // The reproducer from issue #2488: an Integer and a Text threaded through `var`
    // parameters and mutated by the callee, plus the same-typed by-value locals in OnRun
    // that already rendered correctly and must keep doing so. `Bump` mutates TWICE, so
    // the by-ref parameter produces a real series (6 then 16) rather than a single
    // end-of-scope reading — the wrapper's type name is constant, so before the fix
    // AlValueCapture.DiffAndUpdate also saw every observation after the first as
    // "unchanged" and suppressed it. One mutation could not tell those two bugs apart.
    // (The first observation of any scope is a baseline and is never emitted — see
    // AlValueCapture's file header — so `v`'s incoming 5 is legitimately absent.)
    private const string ByRefParamsCode =
        "codeunit 60212 \"CV ByRef Params SX\"\n" +
        "{\n" +
        "    trigger OnRun()\n" +
        "    var\n" +
        "        total: Integer;\n" +
        "        name: Text;\n" +
        "    begin\n" +
        "        total := 5;\n" +
        "        name := 'x';\n" +
        "        Bump(total);\n" +
        "        Rename(name);\n" +
        "    end;\n" +
        "\n" +
        "    local procedure Bump(var v: Integer)\n" +
        "    begin\n" +
        "        v := v + 1;\n" +
        "        v := v + 10;\n" +
        "    end;\n" +
        "\n" +
        "    local procedure Rename(var t: Text)\n" +
        "    begin\n" +
        "        t := 'y';\n" +
        "    end;\n" +
        "}\n";

    [SkippableFact]
    public async Task Execute_CaptureValues_VarParameter_ReportsValue_NotByRefWrapperTypeName()
    {
        TestArtifacts.SkipIfMissing();
        var server = await _fixture.GetAsync();
        var r = await server.SendAsync(JsonSerializer.Serialize(new
        {
            command = "execute",
            captureValues = true,
            code = ByRefParamsCode,
        }));
        var d = JsonSerializer.Deserialize<JsonElement>(r);
        Assert.False(d.TryGetProperty("error", out _), $"unexpected error response: {r}");
        Assert.Equal(0, d.GetProperty("exitCode").GetInt32());

        var captured = d.GetProperty("tests")[0].GetProperty("capturedValues").EnumerateArray().ToList();

        // Nothing anywhere in the series may be a wrapper type name — the whole symptom,
        // stated once over the entire response so a new leak cannot hide in another scope.
        foreach (var e in captured)
        {
            var rendered = e.GetProperty("value").ToString();
            Assert.DoesNotContain("ByRef", rendered);
        }

        // `var v: Integer` — a real JSON number, and the value the callee actually saw.
        var v = captured.Where(e => e.GetProperty("variableName").GetString() == "v").ToList();
        Assert.NotEmpty(v);
        foreach (var e in v)
        {
            Assert.Equal(JsonValueKind.Number, e.GetProperty("value").ValueKind);
            Assert.Null(e.TryGetProperty("captureError", out var ce) ? ce.GetString() : null);
        }
        // Bump receives 5 and mutates it twice: 6, then 16. Both readings must be in the
        // series, in that order — each one read through the wrapper to the caller's slot
        // as it stood at that statement, which is the whole point of a by-ref parameter.
        var vNumbers = v.Select(e => e.GetProperty("value").GetInt32()).ToList();
        Assert.Equal(new[] { 6, 16 }, vNumbers.ToArray());

        // `var t: Text` — the inner NavText's own text, not the wrapper.
        var t = captured.Where(e => e.GetProperty("variableName").GetString() == "t").ToList();
        Assert.NotEmpty(t);
        Assert.Contains("y", t.Select(e => e.GetProperty("value").GetString()));

        // Control, the other direction: the by-value locals in OnRun never went through a
        // wrapper and must render exactly as they always did.
        var total = captured.Where(e => e.GetProperty("variableName").GetString() == "total").ToList();
        Assert.NotEmpty(total);
        Assert.Contains(5, total.Select(e => e.GetProperty("value").GetInt32()));
        var name = captured.Where(e => e.GetProperty("variableName").GetString() == "name").ToList();
        Assert.NotEmpty(name);
        Assert.Contains("x", name.Select(e => e.GetProperty("value").GetString()));
    }
}
