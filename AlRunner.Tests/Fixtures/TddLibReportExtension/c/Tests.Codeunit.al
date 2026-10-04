codeunit 73100 "TREC Tests"
{
    Subtype = Test;

    [Test]
    procedure ReportOfALibrary_ExtendedByAnotherBundle_StartsTheExtensionsTrigger()
    var O: Codeunit "TRE Opener";
    begin
        O.RunOwnReport();
    end;

    [Test]
    procedure NoReport_IsNotAnnotated()
    var N: Integer;
    begin
        N := 1;
    end;
}
