// --tdd and the paths into a stub that no call binds (#5286): a table operation starts a trigger and raises
// database events, and Codeunit.Run runs an OnRun. One run of the TddTriggerEvents fixture (a single folder)
// is shared by the tests here, each asserting the tests of its own shape; the controls start none of them.
//
// Runner-specific (the annotation of a test that reaches a --tdd stub), so it lives here, not in the
// al-language corpus: the claim is about the generator, not about BC.
using Xunit;

namespace AlRunner.Tests;

public sealed class TddTriggerEventsRun : TddRunResult
{
    public TddTriggerEventsRun() : base(Path.Combine(
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..")),
        "AlRunner.Tests", "Fixtures", "TddTriggerEvents")) { }
}

public sealed class TddTriggerEventsTests : IClassFixture<TddTriggerEventsRun>
{
    private readonly TddTriggerEventsRun _run;

    public TddTriggerEventsTests(TddTriggerEventsRun run) => _run = run;

    private static string Stub(string member) => $"Trg Target: procedure \"{member}\"(Arg1: Integer): Integer";

    private void AssertStubs(string test, params string[] members)
    {
        Assert.True(_run.Exit == 0, $"exit {_run.Exit}\n{_run.StdErr}");
        Assert.Equal("pass", _run.Find(test).GetProperty("status").GetString());
        Assert.Equal(members.Select(Stub).OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            _run.StubsOf(test).OrderBy(x => x, StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// Each trigger of the table calls its own missing member. The test that runs the operation firing it
    /// reaches that stub and no other: the operations are told apart, and so is a trigger that runs from
    /// one that does not (a RunTrigger argument that is absent or the literal false).
    /// </summary>
    [SkippableFact]
    public void TableOperation_StartsItsTrigger_OnlyWhenTheTriggerRuns()
    {
        TestArtifacts.SkipIfMissing();

        AssertStubs("InsertTrue_RunsTheOnInsertTrigger", "MissingInsert");
        AssertStubs("ModifyTrue_RunsTheOnModifyTrigger", "MissingModify");
        AssertStubs("DeleteTrue_RunsTheOnDeleteTrigger", "MissingDelete");
        AssertStubs("RenameRunsTheOnRenameTrigger", "MissingRename");
        AssertStubs("ValidateRunsTheFieldOnValidateTrigger", "MissingValidate");
        AssertStubs("DeleteAllTrue_RunsTheOnDeleteTrigger", "MissingDelete");
        AssertStubs("ModifyAllTrue_RunsTheOnModifyTrigger", "MissingModify");
        // Inside the table, with no record variable: the receiver is the table itself.
        AssertStubs("BareInsertInsideTheTable_RunsTheOnInsertTrigger", "MissingInsert");

        // The same operations with no RunTrigger, or with false, run no trigger.
        AssertStubs("InsertWithoutRunTrigger_IsNotAnnotated");
        AssertStubs("InsertFalse_IsNotAnnotated");
        AssertStubs("ModifyAllWithoutRunTrigger_IsNotAnnotated");
    }

    /// <summary>
    /// A database event has no publisher procedure in any bundle: its subscribers are reached by the
    /// operation that raises it, whatever its RunTrigger says. A test that sets its record up with a plain
    /// Insert() raises OnBeforeInsertEvent there, so it names that subscriber's stub too.
    /// </summary>
    [SkippableFact]
    public void TableOperation_RaisesItsDatabaseEvents()
    {
        TestArtifacts.SkipIfMissing();

        AssertStubs("Insert_RaisesOnBeforeInsertEvent", "MissingBeforeInsert");
        AssertStubs("Modify_RaisesOnAfterModifyEvent", "MissingBeforeInsert", "MissingAfterModify");
        AssertStubs("Delete_RaisesOnAfterDeleteEvent", "MissingBeforeInsert", "MissingAfterDelete");
        AssertStubs("Rename_RaisesOnAfterRenameEvent", "MissingBeforeInsert", "MissingAfterRename");
        AssertStubs("Validate_RaisesOnAfterValidateEvent", "MissingAfterValidate");
        // The reviewer's S1: the trigger raises an event declared in the table, a subscriber calls the stub.
        AssertStubs("InsertTrue_RaisesTheEventItsTriggerRaises", "MissingTrigEvent");
    }

    /// <summary>
    /// A subscriber started by a subscriber's own procedure needs a second round of the search: the insert
    /// starts the first subscriber, whose procedure modifies another table, whose subscriber calls the stub.
    /// </summary>
    [SkippableFact]
    public void SubscriberStartedBySubscribersProcedure_IsFollowedThroughTheSecondRound()
    {
        TestArtifacts.SkipIfMissing();

        AssertStubs("InsertIntoTheChainTable_NeedsASecondRound", "MissingChain");
    }

    /// <summary>
    /// Codeunit.Run, by name, on a codeunit variable, with a record argument and through the event its
    /// OnRun raises, reaches the OnRun of that codeunit; running a codeunit that reaches no stub does not.
    /// </summary>
    [SkippableFact]
    public void CodeunitRun_RunsTheOnRunOfTheCodeunitItNames()
    {
        TestArtifacts.SkipIfMissing();

        AssertStubs("CodeunitRunByName_RunsOnRun", "MissingRun");
        AssertStubs("CodeunitRunOfAVariable_RunsOnRun", "MissingRun");
        AssertStubs("CodeunitRunWithARecord_RunsOnRun", "MissingRunRec");
        AssertStubs("CodeunitRun_RaisesTheEventItsOnRunRaises", "MissingRunEvent");
        AssertStubs("CodeunitRunOfAQuietCodeunit_IsNotAnnotated");
        AssertStubs("CodeunitRunOfAQuietVariable_IsNotAnnotated");
    }

    /// <summary>
    /// A RecordRef or a FieldRef names no table the graph can read, so its operation counts for every
    /// table: it reaches the trigger and the subscribers of each, and none of another operation.
    /// </summary>
    [SkippableFact]
    public void RecordRefAndFieldRef_ReachTheTriggersAndSubscribersOfEveryTable()
    {
        TestArtifacts.SkipIfMissing();

        var insert = _run.StubsOf("RecordRefInsertTrue_StartsTheTriggerOfAnyTable");
        foreach (var member in new[] { "MissingInsert", "MissingBeforeInsert", "MissingTrigEvent", "MissingChain" })
            Assert.Contains(Stub(member), insert);
        foreach (var member in new[] { "MissingModify", "MissingDelete", "MissingRename", "MissingRun", "MissingValidate" })
            Assert.DoesNotContain(Stub(member), insert);

        AssertStubs("FieldRefValidate_StartsTheFieldTriggerOfAnyTable", "MissingValidate", "MissingAfterValidate");
    }

    /// <summary>
    /// The controls: a table nothing subscribes to and that has no trigger, and a call named Insert that is
    /// not a record's, reach no stub.
    /// </summary>
    [SkippableFact]
    public void OperationsThatStartNothingWithAStub_AreNotAnnotated()
    {
        TestArtifacts.SkipIfMissing();

        AssertStubs("InsertIntoAQuietTable_IsNotAnnotated");
        AssertStubs("ListInsert_IsNotARecordOperation_IsNotAnnotated");
        AssertStubs("Quiet_IsNotAnnotated");
        // Every member is generated, and the tests naming one are counted: the controls are not among them.
        Assert.Contains("--tdd: generated 15 member(s) this run:", _run.StdErr);
        Assert.Equal(29, _run.Tests.Count);
        Assert.Equal(21, _run.Tests.Count(t => t.TryGetProperty("generatedStubs", out _)));
    }
}
