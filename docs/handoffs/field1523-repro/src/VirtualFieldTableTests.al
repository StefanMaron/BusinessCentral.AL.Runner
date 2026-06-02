// Proves the virtual Field table (2000000041) enumerates a table's REAL field
// metadata. Reproduces the RecoverySolutions EnableWorkflow gap: BC's
// "Library - Workflow".EnableWorkflow iterates Field for TableNo=1523 with
// No.<>1, Type<>BLOB, ObsoleteState<>Removed and throws "There is no Field
// within the filter." when the runner returns zero rows.
codeunit 60491 "VF Virtual Field Table Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit "VF Assert";

    // A Workflow-domain table (1523 = "Workflow Step Instance" / workflow area)
    // whose fields BC's EnableWorkflow enumerates. This is the exact pattern that
    // was returning zero rows.
    [Test]
    procedure WorkflowTable1523_FieldsAreEnumerated()
    var
        FieldRec: Record "Field";
        Count: Integer;
        Field1Seen: Boolean;
    begin
        // Mirror EnableWorkflow's exact filter set.
        FieldRec.SetRange(TableNo, 1523);
        FieldRec.SetFilter("No.", '<>%1', 1);
        FieldRec.SetFilter(Type, '<>%1', FieldRec.Type::BLOB);
        FieldRec.SetFilter(ObsoleteState, '<>%1', FieldRec.ObsoleteState::Removed);

        Count := 0;
        if FieldRec.FindSet() then
            repeat
                Count += 1;
                // No primary-key field 1 must appear (filtered out).
                Assert.IsFalse(FieldRec."No." = 1, 'No. 1 must be filtered out');
                // No BLOB field must appear.
                Assert.IsFalse(FieldRec.Type = FieldRec.Type::BLOB, 'BLOB must be filtered out');
            until FieldRec.Next() = 0;

        // BC returns a non-empty set here — that is the whole point. Zero rows is
        // exactly the gap that made EnableWorkflow throw.
        Assert.IsTrue(Count > 0, 'Table 1523 must expose at least one field through the virtual Field table');
    end;

    // Positive concrete-value assertion: field No.1 of table 1523 exists with a
    // known name/type when not filtered out. Proves real metadata, not a fake row.
    [Test]
    procedure WorkflowTable1523_Field1HasRealMetadata()
    var
        FieldRec: Record "Field";
    begin
        FieldRec.SetRange(TableNo, 1523);
        FieldRec.SetRange("No.", 1);
        Assert.IsTrue(FieldRec.FindFirst(), 'Field 1 of table 1523 must exist');
        Assert.AreEqual(1, FieldRec."No.", 'Field No. must be 1');
        Assert.AreEqual(1523, FieldRec.TableNo, 'TableNo must be 1523');
        Assert.IsTrue(FieldRec.FieldName <> '', 'Field 1 must have a real (non-empty) name');
    end;

    // Cross-check against a Base App table known to work (18 = Customer).
    [Test]
    procedure BaseAppCustomerTable18_FieldsAreEnumerated()
    var
        FieldRec: Record "Field";
        Count: Integer;
    begin
        FieldRec.SetRange(TableNo, 18);
        Count := 0;
        if FieldRec.FindSet() then
            repeat
                Count += 1;
            until FieldRec.Next() = 0;
        Assert.IsTrue(Count > 0, 'Customer table 18 must expose fields');
        // Customer No. (field 1) is the primary key — concrete check.
        FieldRec.Reset();
        FieldRec.SetRange(TableNo, 18);
        FieldRec.SetRange("No.", 1);
        Assert.IsTrue(FieldRec.FindFirst(), 'Customer field 1 must exist');
        Assert.AreEqual('No.', FieldRec.FieldName, 'Customer field 1 is "No."');
    end;

    // Negative: a non-existent table yields zero rows (no fabricated rows).
    [Test]
    procedure NonExistentTable_YieldsNoFields()
    var
        FieldRec: Record "Field";
    begin
        FieldRec.SetRange(TableNo, 1999999); // not a real table
        Assert.IsFalse(FieldRec.FindFirst(), 'A non-existent table must yield no Field rows');
    end;
}
