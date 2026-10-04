codeunit 73070 "TOTC Tests"
{
    Subtype = Test;

    [Test]
    procedure ReportOpenedByIdInALibrary_StartsTheReportsTriggersOfAnotherBundle()
    var O: Codeunit "TOT Opener";
    begin
        O.RunReportById(Report::"TOT B Report");
    end;

    [Test]
    procedure QueryOpenedByIdInALibrary_StartsTheQueriesTriggersOfAnotherBundle()
    var O: Codeunit "TOT Opener";
    begin
        O.SaveQueryById(Query::"TOT B Query");
    end;

    [Test]
    procedure XmlPortRunByIdInALibrary_StartsTheXmlPortsTriggersOfAnotherBundle()
    var O: Codeunit "TOT Opener";
    begin
        O.ExportXmlPortById(XmlPort::"TOT B XmlPort");
    end;

    [Test]
    procedure ReportOfALibrary_ExtendedByAnotherBundle_StartsTheExtensionsTrigger()
    var O: Codeunit "TOT Opener";
    begin
        O.RunOwnReport();
    end;

    [Test]
    procedure NoObject_IsNotAnnotated()
    var R: Record "TOT B Rec";
    begin
        R.PK := 'A';
        R.Insert();
    end;
}
