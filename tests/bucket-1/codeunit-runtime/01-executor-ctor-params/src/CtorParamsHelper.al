codeunit 310000 "ECP Helper"
{
    /// <summary>
    /// Simple helper: multiply two integers.
    /// The test codeunit calls this helper from a Test method that starts with "Test"
    /// but also defines a TestXxx(param) helper — that helper's scope constructor
    /// has extra parameters beyond (parent), which previously caused
    /// "Parameter count mismatch" in the Executor.
    /// </summary>
    procedure Multiply(A: Integer; B: Integer): Integer
    begin
        exit(A * B);
    end;

    procedure Add(A: Integer; B: Integer): Integer
    begin
        exit(A + B);
    end;
}
