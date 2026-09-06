// ServerExecuteCodeunitSelectionTests — which codeunit `execute` runs must be a rule, not a
// property of the AL compiler's TypeDef layout (#3086).
//
// `Program.RunFirstCodeunitOnRun` documented itself as running "the bundle's first OnRun-bearing
// codeunit", and "first" meant "first in Assembly.GetTypes()". The CLR does not define that
// array's order — the same undefined order that made TestExecutor.Run execute test codeunits in
// a shifting order across CI legs (#2801, fixed in #3082). Here the consequence is larger than
// an ordering one: it decides WHICH CODE RUNS AT ALL, and the response says nothing about a
// choice having been made.
//
// Measured on this repo at be7a4de0, before the fix, two `execute` requests each holding two
// OnRun-bearing codeunits:
//
//     code declared 60191 then 60190  ->  {"tests":[{"name":"Codeunit60191.OnRun", ...}]}
//     code declared 60190 then 60191  ->  {"tests":[{"name":"Codeunit60190.OnRun", ...}]}
//
// Same two codeunits, different answer. The rule is now ascending AL object id — the rule test
// codeunits already run under, out of the same helper (TestExecutor.OrderTestCodeunitsByObjectId)
// rather than a second arbitrary one.
//
// WHAT THIS FILE CAN AND CANNOT PROVE. It drives the real runner, so what it observes is the AL
// compiler's layout on this machine composed with the rule. That makes its RED a measurement
// rather than a guarantee: on a machine whose layout already agreed with the rule, the first
// test below would have passed unfixed. TestCodeunitOrderingContractTests carries the
// unconditional half — it hands the helper an input array it controls, so it fails against a
// no-op on any machine. Neither is sufficient alone: that one proves the rule is a rule, this
// one proves `execute` is actually wired to it.
//
// Ghost-test guard: every assertion names the exact codeunit — both the `name` field and a
// value the OnRun trigger itself computed and put in its Error() message, so "the right codeunit
// was selected" cannot be satisfied by a response that merely mentions it.
using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

public class ServerExecuteCodeunitSelectionTests : IClassFixture<SharedCliServer>
{
    private readonly SharedCliServer _fixture;

    public ServerExecuteCodeunitSelectionTests(SharedCliServer fixture) => _fixture = fixture;

    // `Subtype = Test` alone does NOT make a codeunit a test codeunit as far as run-mode
    // selection is concerned — measured: a Subtype=Test codeunit with no [Test] procedure was
    // still picked as the non-test preference. Both Program.RunFirstCodeunitOnRun and
    // TestExecutor.IsTestCodeunit ask the same question, "does it carry a [Test] method", so the
    // fixture has to carry one for the preference below to be under test at all.
    private static string OnRunCodeunit(int id, string name, bool test = false) =>
        $"codeunit {id} \"{name}\" {{ {(test ? "Subtype = Test; " : "")}" +
        $"trigger OnRun() begin Error('ran %1', {id}); end; " +
        (test ? "[Test] procedure ATest() begin end; " : "") + "} ";

    private async Task AssertExecuteRuns(string code, int expectedId)
    {
        var server = await _fixture.GetAsync();
        var r = await server.SendAsync(JsonSerializer.Serialize(new { command = "execute", code }));
        var d = JsonSerializer.Deserialize<JsonElement>(r);
        Assert.False(d.TryGetProperty("error", out _), $"unexpected error response: {r}");
        Assert.False(d.TryGetProperty("compilationErrors", out _), $"unexpected compile error: {r}");

        var tests = d.GetProperty("tests");
        // Exactly one OnRun is run per bundle, whichever one is chosen — a fix that ran BOTH
        // would satisfy a "the right one ran" assertion while changing the contract.
        Assert.Equal(1, tests.GetArrayLength());
        Assert.Equal($"Codeunit{expectedId}.OnRun", tests[0].GetProperty("name").GetString());
        // The trigger's own computed value: proof the body executed, not that the runner merely
        // named a codeunit it never entered.
        Assert.Contains($"ran {expectedId}", tests[0].GetProperty("message").GetString());
    }

    /// <summary>
    /// Positive: the HIGHER id is declared first, so declaration order and object-id order
    /// disagree — the one arrangement that distinguishes "lowest object id" from "whatever came
    /// back first". Measured RED before the fix on this machine (ran 60441).
    /// </summary>
    [SkippableFact]
    public async Task Execute_HigherIdDeclaredFirst_RunsTheLowestObjectId()
    {
        TestArtifacts.SkipIfMissing();
        await AssertExecuteRuns(
            OnRunCodeunit(60441, "Exec Order Zulu SZ") + OnRunCodeunit(60440, "Exec Order Alpha SZ"),
            expectedId: 60440);
    }

    /// <summary>
    /// Control: the same two-codeunit shape with declaration order and id order AGREEING. It
    /// passed before the fix and must still pass — otherwise the change did not make the choice
    /// defined, it merely inverted an arbitrary one.
    /// </summary>
    [SkippableFact]
    public async Task Execute_LowerIdDeclaredFirst_StillRunsTheLowestObjectId()
    {
        TestArtifacts.SkipIfMissing();
        await AssertExecuteRuns(
            OnRunCodeunit(60442, "Exec Order Alpha TZ") + OnRunCodeunit(60443, "Exec Order Zulu TZ"),
            expectedId: 60442);
    }

    /// <summary>
    /// Negative, and the rule that outranks the id: `execute` prefers a NON-test codeunit. Here
    /// the test codeunit carries the LOWER id, so sorting by id alone would pick it. It must not
    /// — `execute` is run-mode, and a Subtype=Test codeunit is only ever the fallback when the
    /// bundle has nothing else with an OnRun.
    /// </summary>
    [SkippableFact]
    public async Task Execute_PrefersNonTestCodeunit_EvenWhenATestCodeunitHasTheLowerId()
    {
        TestArtifacts.SkipIfMissing();
        await AssertExecuteRuns(
            OnRunCodeunit(60444, "Exec Order Test UZ", test: true)
            + OnRunCodeunit(60445, "Exec Order Plain UZ"),
            expectedId: 60445);
    }

    /// <summary>
    /// The single-codeunit request — overwhelmingly the common `execute` — is untouched by any
    /// of this. Cheap, and it is the case a regression here would break for every user at once.
    /// </summary>
    [SkippableFact]
    public async Task Execute_SingleOnRunCodeunit_IsUnaffected()
    {
        TestArtifacts.SkipIfMissing();
        await AssertExecuteRuns(OnRunCodeunit(60446, "Exec Order Solo VZ"), expectedId: 60446);
    }
}
