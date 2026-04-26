codeunit 126002 "RIM Test"
{
    Subtype = Test;
    var
        Assert: Codeunit Assert;

    // ── SaveAs* ────────────────────────────────────────────────────────────────

    [Test]
    procedure SaveAsPdf_NoThrow()
    var
        Src: Codeunit "RIM Source";
    begin
        // Positive: SaveAsPdf completes without error in standalone mode.
        Src.SaveAsPdf_NoOp();
        Assert.IsTrue(true, 'SaveAsPdf must not throw');
    end;

    [Test]
    procedure SaveAsExcel_NoThrow()
    var
        Src: Codeunit "RIM Source";
    begin
        // Positive: SaveAsExcel completes without error in standalone mode.
        Src.SaveAsExcel_NoOp();
        Assert.IsTrue(true, 'SaveAsExcel must not throw');
    end;

    [Test]
    procedure SaveAsWord_NoThrow()
    var
        Src: Codeunit "RIM Source";
    begin
        // Positive: SaveAsWord completes without error in standalone mode.
        Src.SaveAsWord_NoOp();
        Assert.IsTrue(true, 'SaveAsWord must not throw');
    end;

    [Test]
    procedure SaveAsHtml_NoThrow()
    var
        Src: Codeunit "RIM Source";
    begin
        // Positive: SaveAsHtml completes without error in standalone mode.
        Src.SaveAsHtml_NoOp();
        Assert.IsTrue(true, 'SaveAsHtml must not throw');
    end;

    [Test]
    procedure SaveAsXml_NoThrow()
    var
        Src: Codeunit "RIM Source";
    begin
        // Positive: SaveAsXml completes without error in standalone mode.
        Src.SaveAsXml_NoOp();
        Assert.IsTrue(true, 'SaveAsXml must not throw');
    end;

    // ── Property getters ──────────────────────────────────────────────────────

    [Test]
    procedure IsReadOnly_ReturnsFalse()
    var
        Src: Codeunit "RIM Source";
        Result: Boolean;
    begin
        // Positive: IsReadOnly returns false in standalone mode.
        Result := Src.IsReadOnly_ReturnsFalse();
        Assert.IsFalse(Result, 'IsReadOnly must return false');
    end;

    [Test]
    procedure ObjectId_ReturnsText()
    var
        Src: Codeunit "RIM Source";
        Result: Text;
    begin
        // Positive: ObjectId(true) returns the report ID as text (non-empty).
        Result := Src.ObjectId_ReturnsText();
        Assert.IsTrue(StrLen(Result) > 0, 'ObjectId must return a non-empty text');
    end;

    [Test]
    procedure WordXmlPart_ReturnsText()
    var
        Src: Codeunit "RIM Source";
        Result: Text;
    begin
        // Positive: WordXmlPart() returns empty text in standalone mode.
        Result := Src.WordXmlPart_ReturnsText();
        // Just checking it compiles and returns a Text value (empty is fine).
        Assert.IsTrue(Result = '', 'WordXmlPart must return empty text in standalone mode');
    end;

    [Test]
    procedure TargetFormat_ReturnsDefault()
    var
        Src: Codeunit "RIM Source";
        Result: ReportFormat;
    begin
        // Positive: TargetFormat() compiles and returns a ReportFormat value without error.
        Result := Src.TargetFormat_ReturnsDefault();
        Assert.IsTrue(true, 'TargetFormat must compile and run without error');
    end;

    // ── Property setters ──────────────────────────────────────────────────────

    [Test]
    procedure Language_SetGet()
    var
        Src: Codeunit "RIM Source";
        Result: Integer;
    begin
        // Positive: Language(1033) compiles and executes without error.
        Result := Src.Language_SetGet();
        Assert.AreEqual(1033, Result, 'Language_SetGet must return expected value 1033');
    end;

    [Test]
    procedure FormatRegion_SetGet()
    var
        Src: Codeunit "RIM Source";
        Result: Text;
    begin
        // Positive: FormatRegion('en-US') compiles and executes without error.
        Result := Src.FormatRegion_SetGet();
        Assert.AreEqual('en-US', Result, 'FormatRegion_SetGet must return expected value en-US');
    end;

    // ── Layout methods ────────────────────────────────────────────────────────

    [Test]
    procedure RdlcLayout_ReturnsFalse()
    var
        Src: Codeunit "RIM Source";
        Result: Boolean;
    begin
        // Negative: RDLCLayout returns false — no layout data in standalone mode.
        Result := Src.RdlcLayout_ReturnsFalse();
        Assert.IsFalse(Result, 'RDLCLayout must return false in standalone mode');
    end;

    [Test]
    procedure WordLayout_ReturnsFalse()
    var
        Src: Codeunit "RIM Source";
        Result: Boolean;
    begin
        // Negative: WordLayout returns false — no layout data in standalone mode.
        Result := Src.WordLayout_ReturnsFalse();
        Assert.IsFalse(Result, 'WordLayout must return false in standalone mode');
    end;

    [Test]
    procedure ExcelLayout_ReturnsFalse()
    var
        Src: Codeunit "RIM Source";
        Result: Boolean;
    begin
        // Negative: ExcelLayout returns false — no layout data in standalone mode.
        Result := Src.ExcelLayout_ReturnsFalse();
        Assert.IsFalse(Result, 'ExcelLayout must return false in standalone mode');
    end;

    [Test]
    procedure DefaultLayout_Compiles()
    var
        Src: Codeunit "RIM Source";
        Result: Boolean;
    begin
        // Positive: DefaultLayout() compiles and returns an enum value.
        Result := Src.DefaultLayout_Compiles();
        Assert.IsTrue(Result, 'DefaultLayout must compile and return true');
    end;

    // ── Run / RunModal ─────────────────────────────────────────────────────────

    [Test]
    procedure Run_NoThrow()
    var
        Src: Codeunit "RIM Source";
    begin
        // Positive: Report.Run() completes without error in standalone mode.
        Src.Run_NoOp();
        Assert.IsTrue(true, 'Run must not throw');
    end;

    [Test]
    procedure RunModal_NoThrow()
    var
        Src: Codeunit "RIM Source";
    begin
        // Positive: Report.RunModal() completes without error in standalone mode.
        Src.RunModal_NoOp();
        Assert.IsTrue(true, 'RunModal must not throw');
    end;

    [Test]
    procedure RunRequestPage_ReturnsText()
    var
        Src: Codeunit "RIM Source";
        Result: Text;
    begin
        // Positive: RunRequestPage() returns a Text value without error.
        Result := Src.RunRequestPage_ReturnsText();
        // Returns empty string or placeholder XML — just must not throw
        Assert.IsTrue(true, 'RunRequestPage must not throw');
    end;

    // ── SetTableView ──────────────────────────────────────────────────────────

    [Test]
    procedure SetTableView_NoThrow()
    var
        Src: Codeunit "RIM Source";
    begin
        // Positive: SetTableView + Run completes without error.
        Src.SetTableView_NoOp();
        Assert.IsTrue(true, 'SetTableView + Run must not throw');
    end;
}
