// Issue #3236. Two identical codeunits on purpose: the runner does not promise to run test
// codeunits in id order, and whichever of the two runs second starts from a codeunit-boundary
// restore of an install baseline that carries Object Metadata's synthesised rows. Before the
// fix that replay disarmed the #2771 refusal for the rest of the process, so the payload reads
// below answered blank. Each codeunit asserts on entry, so the pair proves it in either order.
codeunit 65582 "OMBR Tests A"
{
    Subtype = Test;
    TestPermissions = Disabled;

    var
        Assert: Codeunit "OMBR Assert";

    [Test]
    procedure PayloadScalar_StillRefuses_AfterBaselineRestore_A()
    var
        ObjectMetadata: Record "Object Metadata";
        Sink: Text;
    begin
        FindOwnRow(ObjectMetadata);

        asserterror Sink := Format(ObjectMetadata."Metadata Version");
        Assert.ExpectedError('out-of-scope: Object Metadata."Metadata Version" (system table 2000000071)');

        asserterror Sink := Format(ObjectMetadata."Has Subscribers");
        Assert.ExpectedError('out-of-scope: Object Metadata."Has Subscribers" (system table 2000000071)');
    end;

    [Test]
    procedure PayloadBlob_StillRefuses_AfterBaselineRestore_A()
    var
        ObjectMetadata: Record "Object Metadata";
    begin
        FindOwnRow(ObjectMetadata);

        asserterror ObjectMetadata.CalcFields(Metadata);
        Assert.ExpectedError('out-of-scope: Object Metadata."Metadata" (system table 2000000071)');
    end;

    [Test]
    procedure KeyColumns_StillRead_AfterBaselineRestore_A()
    var
        ObjectMetadata: Record "Object Metadata";
    begin
        // The control: the fix must not refuse or drop the rows themselves. FindLast landing on
        // the highest listed id fails against an empty or single-placeholder store.
        ObjectMetadata.SetRange("Object Type", ObjectMetadata."Object Type"::Table);
        Assert.IsTrue(ObjectMetadata.FindLast(), 'FindLast over Object Type = Table must succeed after a restore.');
        Assert.AreEqual(2000000400, ObjectMetadata."Object ID", 'FindLast must land on the highest application-database table id.');
        Assert.IsTrue(ObjectMetadata."Emit Version" >= 27000, 'Emit Version has a real source and must still read.');
    end;

    local procedure FindOwnRow(var ObjectMetadata: Record "Object Metadata")
    begin
        ObjectMetadata.SetRange("Object Type", ObjectMetadata."Object Type"::Table);
        ObjectMetadata.SetRange("Object ID", 2000000071);
        Assert.IsTrue(ObjectMetadata.FindFirst(), 'Object Metadata must have a row for its own table id.');
    end;
}
