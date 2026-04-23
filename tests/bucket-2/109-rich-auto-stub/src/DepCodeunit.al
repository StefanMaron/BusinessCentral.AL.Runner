/// Simulates a dependency codeunit from the test toolkit range (130000-139999).
/// This codeunit is compiled from source in this test, but the test verifies
/// that auto-stubbing produces a class with callable methods (not just empty).
/// In real usage, this codeunit would come from an .app package and be
/// auto-stubbed at the C# level.
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
}
