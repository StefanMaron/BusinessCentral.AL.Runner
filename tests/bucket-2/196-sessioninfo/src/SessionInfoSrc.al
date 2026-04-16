/// Exercises SessionInformation static methods — SqlRowsRead, SqlStatementsExecuted,
/// AITokensUsed, Callstack. All are telemetry counters that return safe defaults
/// in standalone mode (no real DB or AI backend).
codeunit 60230 "SI Src"
{
    procedure GetSqlRowsRead(): BigInteger
    begin
        exit(SessionInformation.SqlRowsRead());
    end;

    procedure GetSqlStatementsExecuted(): BigInteger
    begin
        exit(SessionInformation.SqlStatementsExecuted());
    end;

    procedure GetCallstack(): Text
    begin
        exit(SessionInformation.Callstack());
    end;
}
