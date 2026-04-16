/// Helper codeunit that exercises the AL ternary (inline if-then-else) expression.
/// Syntax: `if <Condition> then <TrueExpr> else <FalseExpr>` used as a value expression.
/// Added in BC 22+. The BC compiler emits this as a C# conditional expression;
/// the runner must compile and evaluate it correctly.
codeunit 60000 "TE Helper"
{
    /// Returns 'big' when x > 3, 'small' otherwise.
    procedure Classify(x: Integer): Text
    begin
        exit(if x > 3 then 'big' else 'small');
    end;

    /// Ternary used in an assignment, not directly in exit().
    procedure ClassifyAssign(x: Integer): Text
    var
        result: Text;
    begin
        result := if x > 3 then 'big' else 'small';
        exit(result);
    end;

    /// Nested ternary: three-way classification.
    procedure ClassifyThreeWay(x: Integer): Text
    begin
        exit(if x > 10 then 'large' else if x > 3 then 'medium' else 'small');
    end;

    /// Ternary with boolean condition — even vs odd.
    procedure EvenOrOdd(x: Integer): Text
    begin
        exit(if x mod 2 = 0 then 'even' else 'odd');
    end;

    /// Ternary returning Integer (not Text) — proves non-text branches work.
    procedure MaxOf(a: Integer; b: Integer): Integer
    begin
        exit(if a > b then a else b);
    end;
}
