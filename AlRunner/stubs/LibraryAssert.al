// Stub for BC's "Library Assert" codeunit (ID 130).
// Provides method signatures so AL test code compiles.
// At runtime, MockCodeunitHandle routes codeunit 130 calls to MockAssert.
codeunit 130 "Library Assert"
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

    procedure ExpectedErrorCode(ExpectedErrorCode: Text; ExpectedErrorMessage: Text)
    begin
    end;

    procedure ExpectedMessage(ExpectedMessage: Text; ActualMessage: Text)
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
