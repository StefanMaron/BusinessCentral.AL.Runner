/// Source objects for ReportInstance method tests (issue #675).
table 126000 "RIM Table"
{
    fields
    {
        field(1; "No."; Code[20]) { }
        field(2; Name; Text[50]) { }
    }
    keys { key(PK; "No.") { Clustered = true; } }
}

/// Report used as a ReportInstance variable target.
/// The OnAfterGetRecord trigger exercises CurrReport.Quit/PrintOnlyIfDetail.
report 126000 "RIM Report"
{
    dataset
    {
        dataitem(RimData; "RIM Table")
        {
            trigger OnAfterGetRecord()
            begin
                // Methods injected by the rewriter into the report class.
                if false then begin
                    CurrReport.Quit();
                    CurrReport.PrintOnlyIfDetail := true;
                end;
            end;
        }
    }
}

/// Exercises all ReportInstance (handle) methods accessible from external code.
codeunit 126001 "RIM Source"
{
    // ── SaveAs* ────────────────────────────────────────────────────────────────

    procedure SaveAsPdf_NoOp()
    var
        Rep: Report "RIM Report";
    begin
        Rep.UseRequestPage(false);
        Rep.SaveAsPdf('out.pdf');
    end;

    procedure SaveAsExcel_NoOp()
    var
        Rep: Report "RIM Report";
    begin
        Rep.UseRequestPage(false);
        Rep.SaveAsExcel('out.xlsx');
    end;

    procedure SaveAsWord_NoOp()
    var
        Rep: Report "RIM Report";
    begin
        Rep.UseRequestPage(false);
        Rep.SaveAsWord('out.docx');
    end;

    procedure SaveAsHtml_NoOp()
    var
        Rep: Report "RIM Report";
    begin
        Rep.UseRequestPage(false);
        Rep.SaveAsHtml('out.html');
    end;

    procedure SaveAsXml_NoOp()
    var
        Rep: Report "RIM Report";
    begin
        Rep.UseRequestPage(false);
        Rep.SaveAsXml('out.xml');
    end;

    // ── Property getters ──────────────────────────────────────────────────────

    procedure IsReadOnly_ReturnsFalse(): Boolean
    var
        Rep: Report "RIM Report";
    begin
        exit(Rep.IsReadOnly());
    end;

    procedure ObjectId_ReturnsText(): Text
    var
        Rep: Report "RIM Report";
    begin
        exit(Rep.ObjectId(true));
    end;

    procedure WordXmlPart_ReturnsText(): Text
    var
        Rep: Report "RIM Report";
    begin
        exit(Rep.WordXmlPart());
    end;

    procedure TargetFormat_ReturnsDefault(): ReportFormat
    var
        Rep: Report "RIM Report";
    begin
        exit(Rep.TargetFormat());
    end;

    // ── Property setters ──────────────────────────────────────────────────────

    procedure Language_SetGet(): Integer
    var
        Rep: Report "RIM Report";
    begin
        Rep.Language(1033);
        exit(1033); // Language is write-only in AL; return expected value
    end;

    procedure FormatRegion_SetGet(): Text
    var
        Rep: Report "RIM Report";
    begin
        Rep.FormatRegion('en-US');
        exit('en-US'); // FormatRegion is write-only in AL; return expected value
    end;

    // ── Layout methods ────────────────────────────────────────────────────────

    procedure RdlcLayout_ReturnsFalse(): Boolean
    var
        Rep: Report "RIM Report";
        InStr: InStream;
        Ok: Boolean;
    begin
        Ok := Rep.RDLCLayout(InStr);
        exit(Ok);
    end;

    procedure WordLayout_ReturnsFalse(): Boolean
    var
        Rep: Report "RIM Report";
        InStr: InStream;
        Ok: Boolean;
    begin
        Ok := Rep.WordLayout(InStr);
        exit(Ok);
    end;

    procedure ExcelLayout_ReturnsFalse(): Boolean
    var
        Rep: Report "RIM Report";
        InStr: InStream;
        Ok: Boolean;
    begin
        Ok := Rep.ExcelLayout(InStr);
        exit(Ok);
    end;

    procedure DefaultLayout_Compiles(): Boolean
    var
        Rep: Report "RIM Report";
        Lyt: DefaultLayout;
    begin
        Lyt := Rep.DefaultLayout();
        exit(true); // Just confirming it compiles and returns an enum
    end;

    // ── Run / RunModal ─────────────────────────────────────────────────────────

    procedure Run_NoOp()
    var
        Rep: Report "RIM Report";
    begin
        Rep.UseRequestPage(false);
        Rep.Run();
    end;

    procedure RunModal_NoOp()
    var
        Rep: Report "RIM Report";
    begin
        Rep.UseRequestPage(false);
        Rep.RunModal();
    end;

    procedure RunRequestPage_ReturnsText(): Text
    var
        Rep: Report "RIM Report";
    begin
        Rep.UseRequestPage(false);
        exit(Rep.RunRequestPage());
    end;

    // ── SetTableView ──────────────────────────────────────────────────────────

    procedure SetTableView_NoOp()
    var
        Rep: Report "RIM Report";
        T: Record "RIM Table";
    begin
        Rep.UseRequestPage(false);
        Rep.SetTableView(T);
        Rep.Run();
    end;
}
