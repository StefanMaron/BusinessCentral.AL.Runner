/// Helper codeunit exercising AL ternary (inline if) expressions.
/// Ternary form: if Condition then ValueA else ValueB
codeunit 61220 "Ternary Src"
{
    /// Return 'big' when n > 3, 'small' otherwise.
    procedure Classify(n: Integer): Text
    begin
        exit(if n > 3 then 'big' else 'small');
    end;

    /// Return the larger of two integers.
    procedure Max(a: Integer; b: Integer): Integer
    begin
        exit(if a >= b then a else b);
    end;

    /// Nested ternary: classify into 'low', 'mid', 'high'.
    procedure Bucket(n: Integer): Text
    begin
        exit(if n < 10 then 'low' else if n < 100 then 'mid' else 'high');
    end;

    /// Ternary on boolean — flip true↔false.
    procedure FlipBool(b: Boolean): Boolean
    begin
        exit(if b then false else true);
    end;

    /// Proving helper: a+b+1 to verify the codeunit is live.
    procedure AddWithBonus(a: Integer; b: Integer): Integer
    begin
        exit(a + b + 1);
    end;
}
