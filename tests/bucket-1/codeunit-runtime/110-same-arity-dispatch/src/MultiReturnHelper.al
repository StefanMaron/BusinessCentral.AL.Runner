/// Helper codeunit with multiple methods that share the same parameter count
/// but return different types. Exercises the dispatch mechanism when the
/// arg-count fallback must disambiguate by method name (not just arity).
codeunit 294001 "Multi Return Helper"
{
    procedure GetCode(Input: Integer): Code[20]
    begin
        exit(Format(Input));
    end;

    procedure GetInteger(Input: Integer): Integer
    begin
        exit(Input * 3);
    end;

    procedure GetText(Input: Integer): Text
    begin
        exit('T' + Format(Input));
    end;

    procedure GetBoolean(Input: Integer): Boolean
    begin
        exit(Input > 0);
    end;

    procedure GetDecimal(Input: Integer): Decimal
    begin
        exit(Input / 2);
    end;
}
