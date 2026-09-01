// MissingTestDataDiagnosis — the diagnostic half of issue #2240.
//
// THE PROBLEM, MEASURED
//   On a real customer suite (29 tests, BC 28.4) 16 tests failed and FIFTEEN of them failed on
//   a setup record that was not there, not on their own logic:
//       12 x  The Source Code Setup does not exist
//        3 x  Invoice Nos. must have a value in Purchases & Payables Setup
//   The sixteenth was a genuine assertion failure, and it was invisible — buried under fifteen
//   that looked exactly like it. The runner starts from an empty database, and a bare BC error
//   gives the developer no way to tell "this database has no setup data" from "my code is
//   wrong".
//
// WHAT THIS DOES, AND WHAT IT DELIBERATELY DOES NOT DO
//   It ADDS an explanation next to a failure. It never replaces one, never downgrades an
//   outcome, and never touches the failing test's own message, exception type or AL call stack
//   (.claude/rules/loud-failures.md). A test that failed still failed, with BC's own words.
//
// A FALSE POSITIVE IS WORSE THAN NO MESSAGE
//   Telling somebody their genuine bug is a missing-data problem sends them down the wrong path
//   — the same failure #2240 describes, just pointing the other way. So the explanation fires
//   only on EVIDENCE, never on a text pattern alone. Two things must both hold:
//
//     1. The failure NAMES a table, and the name comes from a typed source, not from parsing
//        prose:
//          - a record-not-found error the runner itself raised carries the AL table id, stashed
//            in Exception.Data by TagTable below at the one site that builds it
//            (RecordWritePatches.BuildRecordNotFoundException);
//          - NavTestFieldException carries a TableName property BC populates itself
//            (measured in Microsoft.Dynamics.Nav.Types.dll: CreateNonblank sets `tableName`).
//        BC's own "The {0} does not exist." wording is localized — the en-US resource string
//        lives in Microsoft.Dynamics.Nav.Language.dll and every shipped culture has its own —
//        so matching on it would be a guess that silently stops working off en-US.
//
//     2. That table is GENUINELY EMPTY in the in-memory store right now, summed across every
//        DataAccessSource that materialised it (RecordPatches.TryCensusTable). If the census
//        cannot see the table, the answer is "I don't know" and nothing is said.
//
//   The negative case this buys is the important one, and it is the one the proving tests lead
//   with: a `Rec.Get('NOPE')` against a table that HAS rows produces exactly the same exception
//   type and exactly the same message shape, and gets no explanation, because the evidence is
//   absent.
//
// WHY THE MESSAGE IS ONE LINE
//   #2261 hit this for real: the bundle reporter keeps only line 1 of an EXEC-FAIL message, so
//   a diagnosis whose second line carried the actionable part reached nobody. Per-test messages
//   are not truncated that way today, but a one-line explanation cannot be truncated by anything
//   that shows up later either.
using Microsoft.Dynamics.Nav.Runtime;

namespace AlRunner.Infrastructure;

internal static class MissingTestDataDiagnosis
{
    /// <summary>Exception.Data key carrying the AL table id of a record-not-found failure. A
    /// string key rather than an object one so it survives any dictionary copy.</summary>
    internal const string TableIdDataKey = "al-runner.missing-record.table-id";

    /// <summary>Exception.Data key carrying that table's AL object name.</summary>
    internal const string TableNameDataKey = "al-runner.missing-record.table-name";

    /// <summary>System/virtual tables (Field, AllObj, Table Metadata, ...) are populated by the
    /// runner from loaded metadata, not from a company's data. An empty one is a runner bug, and
    /// pointing the user at --test-data for it would be wrong twice over — the backup does not
    /// carry them either.</summary>
    private const int FirstVirtualTableId = 2_000_000_000;

    /// <summary>
    /// Record which AL table a record-not-found exception is about, at the one site that builds
    /// it. Returns the same exception so call sites stay a single expression.
    ///
    /// Additive only: Exception.Data is not part of the message, the type or the stack, so
    /// nothing about how the failure reaches the user changes here.
    /// </summary>
    internal static Exception TagTable(Exception ex, object? metaTable)
    {
        if (metaTable is NCLMetaTable meta)
        {
            try
            {
                ex.Data[TableIdDataKey] = meta.TableId;
                ex.Data[TableNameDataKey] = meta.TableName ?? "";
            }
            catch (ArgumentException) { /* a Data dictionary that refuses the key is not worth a failure */ }
            catch (NotSupportedException) { }
        }
        return ex;
    }

    /// <summary>
    /// The one-line explanation for this failure, or null when there is no evidence for one.
    /// Null is the common answer and the safe one.
    ///
    /// Called from TestExecutor.RunOne's catch blocks, which is the last moment the store still
    /// holds exactly what the test saw — the next codeunit/test boundary restores the install
    /// baseline over it.
    /// </summary>
    internal static string? Explain(Exception? ex)
    {
        if (ex == null) return null;
        if (!TryNameTable(ex, out var census)) return null;
        if (census.Rows != 0) return null;                       // the table HAS data — not this
        if (census.TableId >= FirstVirtualTableId) return null;   // see FirstVirtualTableId

        var where = $"'{census.TableName}' (table {census.TableId})";
        if (!TestDataOptions.Enabled)
            return $"[test-data] {where} has no rows in this run, so this failure may be missing "
                 + "setup data rather than a bug in the code under test. The runner starts from an "
                 + "empty database; pass --test-data to load a company out of the BC backup that "
                 + "ships inside the artifact.";

        var outcome = TestDataProvisioner.TableOutcome(census.TableId);
        if (outcome != null)
            return $"[test-data] {where} still has no rows although --test-data is on: {outcome}. "
                 + "So this failure may be missing setup data rather than a bug in the code under test.";

        return $"[test-data] {where} has no rows in this run and --test-data never loaded it — "
             + (TestDataProvisioner.IsArmed
                 ? "the on-demand loader was never asked for this table."
                 : "no backup plan is armed for this app group yet.")
             + " So this failure may be missing setup data rather than a bug in the code under test.";
    }

    /// <summary>
    /// Resolve the failure to exactly one table the store can answer for. Ordered by how strong
    /// the evidence is: an id the runner itself recorded, then a table name BC put on a typed
    /// property. Anything else is not evidence and returns false.
    /// </summary>
    private static bool TryNameTable(Exception ex, out AlRunner.Patches.RecordPatches.StoredTableCensus census)
    {
        census = default!;
        for (var e = ex; e != null; e = e.InnerException)
        {
            if (e.Data[TableIdDataKey] is int taggedId
                && AlRunner.Patches.RecordPatches.TryCensusTable(taggedId, out census))
                return true;

            if (e is Microsoft.Dynamics.Nav.Types.NavTestFieldException testField
                && !string.IsNullOrWhiteSpace(testField.TableName)
                && AlRunner.Patches.RecordPatches.TryCensusTableByName(testField.TableName, out census))
                return true;
        }
        return false;
    }
}
