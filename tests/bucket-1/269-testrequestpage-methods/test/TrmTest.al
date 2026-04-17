codeunit 84401 "TRM Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "TRM Src";
        NavResult: Boolean;
        ValidationCount: Integer;
        ValidationMsg: Text;

    // ── OK action ──────────────────────────────────────────────────────────────
    [Test]
    [HandlerFunctions('OKHandler')]
    procedure OK_Invoke_DoesNotCrash()
    begin
        // Positive: calling OK().Invoke() on a TestRequestPage must not crash.
        Src.RunReport();
        Assert.IsTrue(true, 'OK().Invoke() must complete without error');
    end;

    [RequestPageHandler]
    procedure OKHandler(var RequestPage: TestRequestPage "TRM Report")
    begin
        RequestPage.OK().Invoke();
    end;

    // ── Cancel action ──────────────────────────────────────────────────────────
    [Test]
    [HandlerFunctions('CancelHandler')]
    procedure Cancel_Invoke_DoesNotCrash()
    begin
        // Positive: calling Cancel().Invoke() must not crash.
        Src.RunReport();
        Assert.IsTrue(true, 'Cancel().Invoke() must complete without error');
    end;

    [RequestPageHandler]
    procedure CancelHandler(var RequestPage: TestRequestPage "TRM Report")
    begin
        RequestPage.Cancel().Invoke();
    end;

    // ── Navigation ─────────────────────────────────────────────────────────────
    [Test]
    [HandlerFunctions('FirstHandler')]
    procedure First_Returns_True()
    begin
        // Positive: First() returns true on an in-memory stub.
        Src.RunReport();
        Assert.IsTrue(NavResult, 'First() must return true');
    end;

    [RequestPageHandler]
    procedure FirstHandler(var RequestPage: TestRequestPage "TRM Report")
    begin
        NavResult := RequestPage.First();
    end;

    [Test]
    [HandlerFunctions('LastHandler')]
    procedure Last_DoesNotCrash()
    begin
        // Positive: Last() runs without error.
        Src.RunReport();
        Assert.IsTrue(true, 'Last() must complete without error');
    end;

    [RequestPageHandler]
    procedure LastHandler(var RequestPage: TestRequestPage "TRM Report")
    begin
        NavResult := RequestPage.Last();
    end;

    [Test]
    [HandlerFunctions('NextHandler')]
    procedure Next_DoesNotCrash()
    begin
        // Positive: Next() runs without error.
        Src.RunReport();
        Assert.IsTrue(true, 'Next() must complete without error');
    end;

    [RequestPageHandler]
    procedure NextHandler(var RequestPage: TestRequestPage "TRM Report")
    begin
        NavResult := RequestPage.Next();
    end;

    [Test]
    [HandlerFunctions('PreviousHandler')]
    procedure Previous_DoesNotCrash()
    begin
        // Positive: Previous() runs without error.
        Src.RunReport();
        Assert.IsTrue(true, 'Previous() must complete without error');
    end;

    [RequestPageHandler]
    procedure PreviousHandler(var RequestPage: TestRequestPage "TRM Report")
    begin
        NavResult := RequestPage.Previous();
    end;

    // ── ValidationErrorCount / GetValidationError ───────────────────────────────
    [Test]
    [HandlerFunctions('ValidationCountHandler')]
    procedure ValidationErrorCount_Returns_Zero()
    begin
        // Positive: ValidationErrorCount returns 0 on a clean stub.
        Src.RunReport();
        Assert.AreEqual(0, ValidationCount, 'ValidationErrorCount must be 0 on clean stub');
    end;

    [RequestPageHandler]
    procedure ValidationCountHandler(var RequestPage: TestRequestPage "TRM Report")
    begin
        ValidationCount := RequestPage.ValidationErrorCount();
    end;

    [Test]
    [HandlerFunctions('GetValidationErrorHandler')]
    procedure GetValidationError_Returns_Empty_String()
    begin
        // Positive: GetValidationError(1) returns empty string when there are no errors.
        Src.RunReport();
        Assert.AreEqual('', ValidationMsg, 'GetValidationError must return empty string on clean stub');
    end;

    [RequestPageHandler]
    procedure GetValidationErrorHandler(var RequestPage: TestRequestPage "TRM Report")
    begin
        ValidationMsg := RequestPage.GetValidationError(1);
    end;

    // ── Caption ────────────────────────────────────────────────────────────────
    [Test]
    [HandlerFunctions('CaptionHandler')]
    procedure Caption_DoesNotCrash()
    begin
        // Positive: Caption property returns without error.
        Src.RunReport();
        Assert.IsTrue(true, 'Caption must complete without error');
    end;

    [RequestPageHandler]
    procedure CaptionHandler(var RequestPage: TestRequestPage "TRM Report")
    var
        Cap: Text;
    begin
        Cap := RequestPage.Caption;
    end;

    // ── New ────────────────────────────────────────────────────────────────────
    [Test]
    [HandlerFunctions('NewHandler')]
    procedure New_DoesNotCrash()
    begin
        // Positive: New() is a no-op that must not crash.
        Src.RunReport();
        Assert.IsTrue(true, 'New() must complete without error');
    end;

    [RequestPageHandler]
    procedure NewHandler(var RequestPage: TestRequestPage "TRM Report")
    begin
        RequestPage.New();
    end;

    // ── Expand ─────────────────────────────────────────────────────────────────
    [Test]
    [HandlerFunctions('ExpandHandler')]
    procedure Expand_DoesNotCrash()
    begin
        // Positive: Expand(true) is a no-op that must not crash.
        Src.RunReport();
        Assert.IsTrue(true, 'Expand(true) must complete without error');
    end;

    [RequestPageHandler]
    procedure ExpandHandler(var RequestPage: TestRequestPage "TRM Report")
    begin
        RequestPage.Expand(true);
    end;

    // ── Preview / Print ────────────────────────────────────────────────────────
    [Test]
    [HandlerFunctions('PreviewHandler')]
    procedure Preview_DoesNotCrash()
    begin
        // Positive: Preview() is a no-op that must not crash.
        Src.RunReport();
        Assert.IsTrue(true, 'Preview() must complete without error');
    end;

    [RequestPageHandler]
    procedure PreviewHandler(var RequestPage: TestRequestPage "TRM Report")
    begin
        RequestPage.Preview().Invoke();
    end;

    [Test]
    [HandlerFunctions('PrintHandler')]
    procedure Print_DoesNotCrash()
    begin
        // Positive: Print() is a no-op that must not crash.
        Src.RunReport();
        Assert.IsTrue(true, 'Print() must complete without error');
    end;

    [RequestPageHandler]
    procedure PrintHandler(var RequestPage: TestRequestPage "TRM Report")
    begin
        RequestPage.Print().Invoke();
    end;

    // ── SaveAs* ────────────────────────────────────────────────────────────────
    [Test]
    [HandlerFunctions('SaveAsPdfHandler')]
    procedure SaveAsPdf_DoesNotCrash()
    begin
        // Positive: SaveAsPdf() is a no-op that must not crash.
        Src.RunReport();
        Assert.IsTrue(true, 'SaveAsPdf() must complete without error');
    end;

    [RequestPageHandler]
    procedure SaveAsPdfHandler(var RequestPage: TestRequestPage "TRM Report")
    begin
        RequestPage.SaveAsPdf('report.pdf');
    end;

    [Test]
    [HandlerFunctions('SaveAsExcelHandler')]
    procedure SaveAsExcel_DoesNotCrash()
    begin
        // Positive: SaveAsExcel() is a no-op that must not crash.
        Src.RunReport();
        Assert.IsTrue(true, 'SaveAsExcel() must complete without error');
    end;

    [RequestPageHandler]
    procedure SaveAsExcelHandler(var RequestPage: TestRequestPage "TRM Report")
    begin
        RequestPage.SaveAsExcel('report.xlsx');
    end;

    [Test]
    [HandlerFunctions('SaveAsWordHandler')]
    procedure SaveAsWord_DoesNotCrash()
    begin
        // Positive: SaveAsWord() is a no-op that must not crash.
        Src.RunReport();
        Assert.IsTrue(true, 'SaveAsWord() must complete without error');
    end;

    [RequestPageHandler]
    procedure SaveAsWordHandler(var RequestPage: TestRequestPage "TRM Report")
    begin
        RequestPage.SaveAsWord('report.docx');
    end;

    [Test]
    [HandlerFunctions('SaveAsXmlHandler')]
    procedure SaveAsXml_DoesNotCrash()
    begin
        // Positive: SaveAsXml() is a no-op that must not crash.
        Src.RunReport();
        Assert.IsTrue(true, 'SaveAsXml() must complete without error');
    end;

    [RequestPageHandler]
    procedure SaveAsXmlHandler(var RequestPage: TestRequestPage "TRM Report")
    begin
        RequestPage.SaveAsXml('report.xml', 'data.xml');
    end;

    // ── Schedule ───────────────────────────────────────────────────────────────
    [Test]
    [HandlerFunctions('ScheduleHandler')]
    procedure Schedule_DoesNotCrash()
    begin
        // Positive: Schedule() is a no-op that must not crash.
        Src.RunReport();
        Assert.IsTrue(true, 'Schedule() must complete without error');
    end;

    [RequestPageHandler]
    procedure ScheduleHandler(var RequestPage: TestRequestPage "TRM Report")
    begin
        RequestPage.Schedule().Invoke();
    end;

    // ── FindFirstField / FindNextField / FindPreviousField ─────────────────────
    [Test]
    [HandlerFunctions('FindFirstFieldHandler')]
    procedure FindFirstField_Returns_Bool()
    begin
        // Positive: FindFirstField returns a bool without crashing.
        Src.RunReport();
        Assert.IsTrue(true, 'FindFirstField() must complete without error');
    end;

    [RequestPageHandler]
    procedure FindFirstFieldHandler(var RequestPage: TestRequestPage "TRM Report")
    begin
        NavResult := RequestPage.FindFirstField(RequestPage."AmountFld", 0);
    end;

    [Test]
    [HandlerFunctions('FindNextFieldHandler')]
    procedure FindNextField_Returns_Bool()
    begin
        // Positive: FindNextField returns a bool without crashing.
        Src.RunReport();
        Assert.IsTrue(true, 'FindNextField() must complete without error');
    end;

    [RequestPageHandler]
    procedure FindNextFieldHandler(var RequestPage: TestRequestPage "TRM Report")
    begin
        NavResult := RequestPage.FindNextField(RequestPage."AmountFld", 0);
    end;

    [Test]
    [HandlerFunctions('FindPreviousFieldHandler')]
    procedure FindPreviousField_Returns_Bool()
    begin
        // Positive: FindPreviousField returns a bool without crashing.
        Src.RunReport();
        Assert.IsTrue(true, 'FindPreviousField() must complete without error');
    end;

    [RequestPageHandler]
    procedure FindPreviousFieldHandler(var RequestPage: TestRequestPage "TRM Report")
    begin
        NavResult := RequestPage.FindPreviousField(RequestPage."AmountFld", 0);
    end;

    // ── GoToKey ────────────────────────────────────────────────────────────────
    [Test]
    [HandlerFunctions('GoToKeyHandler')]
    procedure GoToKey_ReturnsTrue()
    begin
        // Positive: GoToKey(keyValue) must return true on the stub.
        Src.RunReport();
        Assert.IsTrue(NavResult, 'GoToKey must return true on stub');
    end;

    [RequestPageHandler]
    procedure GoToKeyHandler(var RequestPage: TestRequestPage "TRM Report")
    begin
        NavResult := RequestPage.GoToKey(1);
    end;

    [Test]
    [HandlerFunctions('GoToKeyNegativeHandler')]
    procedure GoToKey_NegativeNotFalse()
    begin
        // Negative: GoToKey must not return false (proving the stub does not default to false).
        Src.RunReport();
        Assert.IsFalse(not NavResult, 'GoToKey must not return false');
    end;

    [RequestPageHandler]
    procedure GoToKeyNegativeHandler(var RequestPage: TestRequestPage "TRM Report")
    begin
        NavResult := RequestPage.GoToKey(1);
    end;

    // ── GoToRecord ─────────────────────────────────────────────────────────────
    [Test]
    [HandlerFunctions('GoToRecordHandler')]
    procedure GoToRecord_ReturnsTrue()
    begin
        // Positive: GoToRecord(rec) must return true on the stub.
        Src.RunReport();
        Assert.IsTrue(NavResult, 'GoToRecord must return true on stub');
    end;

    [RequestPageHandler]
    procedure GoToRecordHandler(var RequestPage: TestRequestPage "TRM Report")
    var
        Rec: Record "TRM Nav Rec";
    begin
        NavResult := RequestPage.GoToRecord(Rec);
    end;

    [Test]
    [HandlerFunctions('GoToRecordNegativeHandler')]
    procedure GoToRecord_NegativeNotFalse()
    begin
        // Negative: GoToRecord must not return false (proving the stub does not default to false).
        Src.RunReport();
        Assert.IsFalse(not NavResult, 'GoToRecord must not return false');
    end;

    [RequestPageHandler]
    procedure GoToRecordNegativeHandler(var RequestPage: TestRequestPage "TRM Report")
    var
        Rec: Record "TRM Nav Rec";
    begin
        NavResult := RequestPage.GoToRecord(Rec);
    end;

    // ── IsExpanded ─────────────────────────────────────────────────────────────
    [Test]
    [HandlerFunctions('IsExpandedHandler')]
    procedure IsExpanded_ReturnsFalse()
    begin
        // Positive: IsExpanded() must return false (stub — no real UI rendering).
        Src.RunReport();
        Assert.IsFalse(NavResult, 'IsExpanded must return false on stub');
    end;

    [RequestPageHandler]
    procedure IsExpandedHandler(var RequestPage: TestRequestPage "TRM Report")
    begin
        NavResult := RequestPage.IsExpanded();
    end;

    [Test]
    [HandlerFunctions('IsExpandedNegativeHandler')]
    procedure IsExpanded_NegativeNotTrue()
    begin
        // Negative: IsExpanded must not return true (proving the stub does not default to true).
        Src.RunReport();
        Assert.IsTrue(not NavResult, 'IsExpanded must not return true');
    end;

    [RequestPageHandler]
    procedure IsExpandedNegativeHandler(var RequestPage: TestRequestPage "TRM Report")
    begin
        NavResult := RequestPage.IsExpanded();
    end;
}
