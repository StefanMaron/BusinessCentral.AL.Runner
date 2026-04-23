/// Simulates a dependency codeunit from the test toolkit range (130000-139999).
/// This codeunit is compiled from source in this test, but the test verifies
/// that auto-stubbing produces a class with callable methods (not just empty).
/// In real usage, this codeunit would come from an .app package and be
/// auto-stubbed via AL compilation from SymbolReference.json metadata.
codeunit 130999 "Rich Stub Helper"
{
    procedure ComputeValue(Input: Integer): Integer
    begin
        exit(Input * 2);
    end;

    procedure FormatLabel(Prefix: Text; Value: Integer): Text
    begin
        exit(Prefix + Format(Value));
    end;

    /// Simulates a void method on an auto-stubbed codeunit.
    /// Auto-stubs generate empty method bodies — calling them must not crash.
    procedure DoSetup()
    begin
        // In a real auto-stub this body would be empty.
        // We leave it empty here to simulate the auto-stub pattern.
    end;

    /// Simulates an auto-stubbed Boolean return method.
    /// Auto-stubs return default(Boolean) = false.
    procedure IsReady(): Boolean
    begin
        exit(false);
    end;

    /// Simulates an auto-stubbed Decimal return method.
    /// Auto-stubs return default(Decimal) = 0.
    procedure GetAmount(): Decimal
    begin
        exit(0);
    end;

    /// Simulates an auto-stubbed Code return method.
    /// Auto-stubs return default(Code) = '' (empty).
    procedure GetCode(): Code[20]
    begin
        exit('');
    end;

    /// Method with multiple parameters — tests that auto-stub dispatch
    /// handles multi-arg methods correctly, not just single-arg ones.
    procedure Combine(A: Integer; B: Integer; Prefix: Text): Text
    begin
        exit(Prefix + Format(A + B));
    end;
}
