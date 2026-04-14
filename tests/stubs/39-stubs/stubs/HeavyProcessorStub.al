codeunit 53901 "Heavy Processor"
{
    /// Stub: replaces the real Heavy Processor for testing.
    /// Doubles the value instead of tripling (so we can verify the stub is used).
    procedure ProcessData(var Rec: Record "Stub Table"): Boolean
    begin
        Rec."Value" := Rec."Value" * 2;
        exit(true);
    end;
}
