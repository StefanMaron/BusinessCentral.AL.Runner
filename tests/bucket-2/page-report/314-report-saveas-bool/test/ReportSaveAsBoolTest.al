/// Tests that Report.SaveAs / SaveAsPdf / SaveAsWord / SaveAsExcel / SaveAsHtml / SaveAsXml
/// compile and return Boolean (true) when used in an `if` expression.
/// Regression for: CS0019 on '&' when static overloads returned void instead of bool.
codeunit 1319002 "Report SaveAs Bool Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure ReportSaveAs_UsedAsBoolean_ReturnsTrue()
    var
        Src: Codeunit "Report SaveAs Bool Src";
    begin
        // [GIVEN] Report.SaveAs called with OutStream overload in an if-expression
        // [WHEN]  Src.SaveAsReturnsTrue returns the Boolean result
        // [THEN]  Must be true (no-op stub returns true, matching BC semantics)
        Assert.IsTrue(Src.SaveAsReturnsTrue(99999, ''), 'Report.SaveAs 4-arg must return true');
    end;

    [Test]
    procedure ReportSaveAs_RecordRef_UsedAsBoolean_ReturnsTrue()
    var
        Src: Codeunit "Report SaveAs Bool Src";
    begin
        // [GIVEN] Report.SaveAs called with 5-arg (RecordRef) overload in an if-expression
        // [WHEN]  Src.SaveAsRecordRefReturnsTrue returns the Boolean result
        // [THEN]  Must be true
        Assert.IsTrue(Src.SaveAsRecordRefReturnsTrue(99999, ''), 'Report.SaveAs 5-arg must return true');
    end;

    [Test]
    procedure ReportSaveAsPdf_UsedAsBoolean_ReturnsTrue()
    var
        Src: Codeunit "Report SaveAs Bool Src";
    begin
        Assert.IsTrue(Src.SaveAsPdfReturnsTrue(99999, ''), 'Report.SaveAsPdf must return true');
    end;

    [Test]
    procedure ReportSaveAsWord_UsedAsBoolean_ReturnsTrue()
    var
        Src: Codeunit "Report SaveAs Bool Src";
    begin
        Assert.IsTrue(Src.SaveAsWordReturnsTrue(99999, ''), 'Report.SaveAsWord must return true');
    end;

    [Test]
    procedure ReportSaveAsExcel_UsedAsBoolean_ReturnsTrue()
    var
        Src: Codeunit "Report SaveAs Bool Src";
    begin
        Assert.IsTrue(Src.SaveAsExcelReturnsTrue(99999, ''), 'Report.SaveAsExcel must return true');
    end;

    [Test]
    procedure ReportSaveAsHtml_UsedAsBoolean_ReturnsTrue()
    var
        Src: Codeunit "Report SaveAs Bool Src";
    begin
        Assert.IsTrue(Src.SaveAsHtmlReturnsTrue(99999, ''), 'Report.SaveAsHtml must return true');
    end;

    [Test]
    procedure ReportSaveAsXml_UsedAsBoolean_ReturnsTrue()
    var
        Src: Codeunit "Report SaveAs Bool Src";
    begin
        Assert.IsTrue(Src.SaveAsXmlReturnsTrue(99999, ''), 'Report.SaveAsXml must return true');
    end;
}
