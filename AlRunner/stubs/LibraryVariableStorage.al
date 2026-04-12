// Stub for BC's Library - Variable Storage codeunit (ID 131004).
// At runtime, MockCodeunitHandle routes codeunit 131004 calls to MockVariableStorage.
codeunit 131004 "Library - Variable Storage"
{
    procedure Enqueue(Value: Variant)
    begin
    end;

    procedure DequeueText(): Text
    begin
    end;

    procedure DequeueInteger(): Integer
    begin
    end;

    procedure DequeueDecimal(): Decimal
    begin
    end;

    procedure DequeueBoolean(): Boolean
    begin
    end;

    procedure DequeueDate(): Date
    begin
    end;

    procedure DequeueVariant(): Variant
    begin
    end;

    procedure AssertEmpty()
    begin
    end;

    procedure Clear()
    begin
    end;

    procedure IsEmpty(): Boolean
    begin
    end;
}
