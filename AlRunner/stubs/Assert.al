// Stub for BC's "Library Assert" codeunit (ID 131).
// The BC test toolkit uses both "Library Assert" (newer) and "Assert" (older) names.
// ID 130 ("Assert") is in LibraryAssert.al for backward compatibility.
// ID 131 ("Library Assert") allows projects that reference Codeunit "Library Assert" to compile.
// At runtime, MockCodeunitHandle routes codeunit 131 calls to MockAssert.
codeunit 131 "Library Assert"
{
    procedure AreEqual(Expected: Variant; Actual: Variant; Msg: Text)
    begin
    end;

    procedure AreNotEqual(Expected: Variant; Actual: Variant; Msg: Text)
    begin
    end;

    procedure IsTrue(Condition: Boolean; Msg: Text)
    begin
    end;

    procedure IsFalse(Condition: Boolean; Msg: Text)
    begin
    end;

    procedure ExpectedError(ExpectedErrorMessage: Text)
    begin
    end;

    procedure ExpectedErrorCode(ExpectedErrorCode: Text)
    begin
    end;

    procedure ExpectedErrorCode(ExpectedErrorCode: Text; ExpectedErrorMessage: Text)
    begin
    end;

    procedure ExpectedMessage(ExpectedMessage: Text; ActualMessage: Text)
    begin
    end;

    procedure ExpectedTestFieldError(FieldCaption: Text; FieldValue: Text)
    begin
    end;

    procedure RecordIsEmpty(RecordVariant: Variant)
    begin
    end;

    procedure RecordIsNotEmpty(RecordVariant: Variant)
    begin
    end;

    procedure RecordCount(RecordVariant: Variant; ExpectedCount: Integer)
    begin
    end;

    procedure TableIsEmpty(TableId: Integer)
    begin
    end;

    procedure TableIsNotEmpty(TableId: Integer)
    begin
    end;

    procedure AreNearlyEqual(Expected: Decimal; Actual: Decimal; Delta: Decimal; Msg: Text)
    begin
    end;

    procedure Fail(Msg: Text)
    begin
    end;
}
