/// <summary>
/// End-to-end proof for issue #2289: a table's AutoIncrement field must not hand out a key
/// `--test-data` hydration already put in storage.
///
/// The repro from the issue, exactly: with `--test-data` pointed at the CRONUS sandbox
/// backup and the Tests-TestLibraries dependency declared (pulling in System Application
/// Test Library), `Codeunit138704.OnInstallAppPerCompany`'s own install-time logging calls
/// `Codeunit3909."Retention Policy Log Impl.".CreateLogEntry`, which does a bare
/// `RetentionPolicyLogEntry.Insert()` with no explicit "Entry No." — table 3905's
/// AutoIncrement field (BigInteger, `AutoIncrement = true`, no application-level FindLast at
/// all — see RetentionPolicyLogImpl.Codeunit.al / RetentionPolicyLogEntry.Table.al inside
/// Microsoft's own System Application source) is what BC uses to pick the number. The CRONUS
/// backup holds 63 real rows for this table (Entry No. 1-63, measured directly with `bcbak
/// read`), and before this fix the runner's own AutoIncrement counter (BcRuntime._aiCounters)
/// was a per-tableId in-memory value that started at 0/1 and was bumped ONLY by
/// NavRecord.ALInsertAsync — never by --test-data's own reflection-based row insert
/// (RecordPatches.TestDataHydration.cs bypasses ALInsertAsync on purpose, since replaying a
/// backup through AL Insert would run triggers a restore never runs). Result: the very next
/// AL Insert into an already-hydrated table reused a number the backup already occupied and
/// NavCSideDuplicateKeyException aborted the whole install trigger before a single [Test]
/// method ran (exit 3, 0 tests).
///
/// Table 3905 "Retention Policy Log Entry" is `Access = Internal` inside System Application,
/// so this test opens it via RecordRef(3905) rather than a static `Record "..."` variable —
/// Access modifiers are a compile-time restriction on static type references, not on a
/// runtime-resolved RecordRef, and BC honours that distinction the same way here.
///
/// NOT RUN BY CI — see tests/test-data-fixture/README.md. This bundle only passes with
/// --test-data and a BC sandbox backup on the machine, same reason that fixture has its own
/// directory. It is a SEPARATE app from tests/test-data-fixture/ (own app.json, own idRange)
/// because it needs the Tests-TestLibraries dependency to reach System Application's own
/// install triggers, which the other fixture deliberately carries none of.
/// </summary>
codeunit 65300 "TDF AutoInc After Hydration"
{
    Subtype = Test;

    /// <summary>
    /// Reaching this line at all is half the proof: TestExecutor runs every dependency's
    /// install triggers BEFORE any [Test] method executes, so a suite that gets here has
    /// already survived Codeunit138704.OnInstallAppPerCompany without the
    /// NavCSideDuplicateKeyException the issue reports. The count assertion is the other
    /// half — the CONCRETE claim that the backup's own 63 rows (measured in the issue via
    /// `bcbak read`, not assumed) are genuinely present, not merely "the table exists".
    /// A build that hydrated nothing, or hydrated into a table the install trigger never
    /// touches, would still reach this line; only the count catches that.
    /// </summary>
    [Test]
    procedure RetentionPolicyLogEntry_HasHydratedRowsAfterInstall()
    var
        RetentionPolicyLog: RecordRef;
        RowCount: Integer;
    begin
        RetentionPolicyLog.Open(3905); // "Retention Policy Log Entry" (Access = Internal)
        RowCount := RetentionPolicyLog.Count();
        if RowCount < 63 then
            Error(
                'expected at least 63 rows in Retention Policy Log Entry (the CRONUS backup''s ' +
                'own row count, per issue #2289) plus whatever the install triggers logged on ' +
                'top; found %1. Was this run without --test-data?', RowCount);
    end;

    /// <summary>
    /// The load-bearing proof. Computes the highest "Entry No." already in the table (the
    /// hydrated backup rows PLUS anything the install triggers already logged before this
    /// test ran), inserts one more row through the exact same AutoIncrement mechanism the
    /// install triggers used, and asserts the assigned number is strictly greater than that
    /// pre-existing maximum.
    ///
    /// Before the fix this either throws NavCSideDuplicateKeyException outright (the counter
    /// reused a number already in storage) or — the weaker failure the strict inequality
    /// below exists to catch — could coincidentally assign a number that happens not to
    /// collide without actually being derived from the table's real high-water mark. Asserting
    /// a bare "no exception" would pass on the first shape only.
    /// </summary>
    [Test]
    procedure RetentionPolicyLogEntry_NewInsertAfterHydrationDoesNotCollide()
    var
        ScanCursor: RecordRef;
        InsertCursor: RecordRef;
        EntryNoField: FieldRef;
        MaxEntryNoBefore: BigInteger;
        CurrentEntryNo: BigInteger;
        NewEntryNo: BigInteger;
    begin
        // A SEPARATE RecordRef for the scan vs. the insert, deliberately: reusing one
        // instance across FindSet/Next and then Init/Insert leaves the buffer positioned on
        // the last row FindSet found, and Init() on a RecordRef does not blank field values
        // the way a freshly-opened one starts blank — so field 1 would still read the LAST
        // SCANNED row's own Entry No. going into Insert(), producing a self-inflicted
        // duplicate-key error that has nothing to do with the AutoIncrement counter this test
        // exists to check. Two independent cursors avoid that confound entirely.
        ScanCursor.Open(3905); // "Retention Policy Log Entry" (Access = Internal)
        if ScanCursor.FindSet() then
            repeat
                EntryNoField := ScanCursor.Field(1); // "Entry No."
                CurrentEntryNo := EntryNoField.Value;
                if CurrentEntryNo > MaxEntryNoBefore then
                    MaxEntryNoBefore := CurrentEntryNo;
            until ScanCursor.Next() = 0;

        if MaxEntryNoBefore = 0 then
            Error('Retention Policy Log Entry read back no rows at all — was this run without --test-data?');

        InsertCursor.Open(3905);
        InsertCursor.Insert(true);
        EntryNoField := InsertCursor.Field(1);
        NewEntryNo := EntryNoField.Value;

        if NewEntryNo <= MaxEntryNoBefore then
            Error(
                'AutoIncrement reused a key --test-data hydration already put in the table ' +
                '(issue #2289): new Entry No. %1 is not greater than the pre-existing max %2.',
                NewEntryNo, MaxEntryNoBefore);
    end;
}
