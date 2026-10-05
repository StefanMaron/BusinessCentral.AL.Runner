/// <summary>#5339: a test codeunit whose only procedure calls one that does not exist, so the compile drops it. Dropping a
/// test codeunit nothing in the bundle names is the one drop the runner lets the module run past.</summary>
codeunit 72212 "MOD Dropped Codeunit"
{
    Subtype = Test;

    [Test]
    procedure Broken()
    begin
        NoSuchProcedure();
    end;
}
