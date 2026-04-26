/// Helper codeunit exercising AL ternary operator: Condition ? ValueA : ValueB
/// (BC 25+ / 2024 Wave 2 syntax — available in BC 26.0+)
codeunit 61220 "Ternary Src"
{
    /// Return 'big' when n > 3, 'small' otherwise.
    procedure Classify(n: Integer): Text
    begin
        exit(n > 3 ? 'big' : 'small');
    end;

    /// Return the larger of two integers.
    procedure Max(a: Integer; b: Integer): Integer
    begin
        exit(a >= b ? a : b);
    end;

    /// Nested ternary: classify into 'low', 'mid', 'high'.
    procedure Bucket(n: Integer): Text
    begin
        exit(n < 10 ? 'low' : n < 100 ? 'mid' : 'high');
    end;

    /// Ternary on boolean — flip true↔false.
    procedure FlipBool(b: Boolean): Boolean
    begin
        exit(b ? false : true);
    end;

    /// Proving helper: a+b+1 to verify the codeunit is live.
    procedure AddWithBonus(a: Integer; b: Integer): Integer
    begin
        exit(a + b + 1);
    end;
}
