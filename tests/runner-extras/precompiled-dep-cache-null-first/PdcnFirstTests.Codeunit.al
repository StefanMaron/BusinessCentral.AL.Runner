// Bundle 1 of #4783's ordered pair. Its only job is the lookup that records table 61600 as
// absent: 61600 is declared by the second bundle's precompiled dependency, whose .app is not
// registered yet while this bundle runs. The assertions that depend on it are in
// tests/runner-extras/xmlport-precompiled-dep-metadata ("XPD Tests").
//
// The whole claim here is "asking does not throw" (tdd.md's _DoesNotThrow exception): in the
// combined tests/runner-extras bundle the dependency IS registered and 61600 is visible, in
// the ordered run it is not, so asserting either answer would be wrong in one of the two runs.
codeunit 66400 "PDCN First Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure AskingAboutTheLaterBundlesDependencyTable_DoesNotThrow()
    var
        TableMetadata: Record "Table Metadata";
        FieldRec: Record "Field";
    begin
        if TableMetadata.Get(61600) then;
        if FieldRec.Get(61600, 1) then;
    end;
}
