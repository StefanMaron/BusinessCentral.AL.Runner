// CalcFormulaRetryBookkeepingTests — issue #3121, differential 1, the bookkeeping half.
//
// A table built while the .app declaring its FlowField's CalcFormula SOURCE table was not
// registered yet gets its metadata rebuilt once that .app registers. This pins the ledger that
// decides which tables that is, and the two properties that keep the rebuild from becoming a
// per-registration sweep:
//
//   * only a table actually noted is pending, and each id is noted once however many of its
//     FlowFields failed;
//   * a reload clears the ledger, because every table is rebuilt from scratch anyway.
//
// The rebuild itself (evict _metaTableCache + the skeleton NCLMetadata entry, repopulate) needs
// a live BC skeleton runtime and is measured end to end instead — see the PR for #3121: a
// source-bearing dependency .app consumed through --package-cache, where
// `CalcFormula = lookup(Customer.Name where(...))` went from BC's "You must define a CalcFormula
// for the Customer Name FlowField" to the calculated value, while a count formula over a table
// in the SAME package passed both before and after.

using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

public class CalcFormulaRetryBookkeepingTests
{
    [Fact]
    public void NoteUnresolvedCalcFormulaSourceTable_RecordsOneEntryPerTable()
    {
        RecordPatches.ClearUnresolvedCalcFormulaTables();
        try
        {
            Assert.Equal(0, RecordPatches.UnresolvedCalcFormulaTableCount);

            RecordPatches.NoteUnresolvedCalcFormulaSourceTable(65630, "Customer");
            Assert.Equal(1, RecordPatches.UnresolvedCalcFormulaTableCount);

            // A second FlowField on the SAME table naming a DIFFERENT source table must not
            // make it two pending tables — the rebuild is per table, and double-counting would
            // rebuild it twice.
            RecordPatches.NoteUnresolvedCalcFormulaSourceTable(65630, "Item");
            Assert.Equal(1, RecordPatches.UnresolvedCalcFormulaTableCount);

            RecordPatches.NoteUnresolvedCalcFormulaSourceTable(65640, "Customer");
            Assert.Equal(2, RecordPatches.UnresolvedCalcFormulaTableCount);
        }
        finally
        {
            RecordPatches.ClearUnresolvedCalcFormulaTables();
        }
    }

    [Fact]
    public void NoteUnresolvedCalcFormulaSourceTable_IgnoresAnUnusableTableIdOrName()
    {
        RecordPatches.ClearUnresolvedCalcFormulaTables();
        try
        {
            // Table id 0 is "no parent table" and an empty name is "no source table named" —
            // neither identifies anything a later registration could resolve, so pending them
            // would schedule a rebuild that cannot change any answer.
            RecordPatches.NoteUnresolvedCalcFormulaSourceTable(0, "Customer");
            RecordPatches.NoteUnresolvedCalcFormulaSourceTable(65630, "");

            Assert.Equal(0, RecordPatches.UnresolvedCalcFormulaTableCount);
        }
        finally
        {
            RecordPatches.ClearUnresolvedCalcFormulaTables();
        }
    }

    [Fact]
    public void ClearUnresolvedCalcFormulaTables_EmptiesTheLedger()
    {
        RecordPatches.NoteUnresolvedCalcFormulaSourceTable(65630, "Customer");
        RecordPatches.NoteUnresolvedCalcFormulaSourceTable(65640, "Item");
        Assert.Equal(2, RecordPatches.UnresolvedCalcFormulaTableCount);

        RecordPatches.ClearUnresolvedCalcFormulaTables();

        Assert.Equal(0, RecordPatches.UnresolvedCalcFormulaTableCount);
    }

}
