codeunit 70665 "PTM Tests"
{
    Subtype = Test;

    // Opening each page is what makes the runner compute that page's trigger metadata; the
    // assertions live in AlRunner.Tests/PageTriggerMetadataTests.cs, over the audit lines.
    [Test]
    procedure OpenEveryPage()
    var
        Row: Record "PTM Row";
        Rich: TestPage "PTM Rich";
        Plain: TestPage "PTM Plain";
        Rowset: TestPage "PTM Rowset";
    begin
        Row."No." := 1;
        Row.Insert();

        Rich.OpenView();
        Rich.Close();

        Plain.OpenView();
        Plain.Close();

        Rowset.OpenView();
        Rowset.Close();
    end;
}
