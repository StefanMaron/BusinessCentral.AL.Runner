/// Helper codeunit that wraps Evaluate() calls on Version variables.
/// BC lowers Evaluate(var VersionVar; Text) to
/// ALSystemVariable.ALEvaluate<NavVersion>(DataError, ByRef<NavVersion>, text, radix).
/// After type rewriting NavVersion → MockVersion the generic constraint
/// 'where T : class' on ALEvaluate<T> fails with CS0452 because MockVersion is a struct.
codeunit 1297001 "Version Evaluate Helper"
{
    /// Parse a dotted version string into a Version variable.
    /// Returns true if Evaluate succeeded, false otherwise.
    procedure TryParseVersion(VersionText: Text; var Result: Version): Boolean
    begin
        exit(Evaluate(Result, VersionText));
    end;

    procedure GetMajor(Ver: Version): Integer
    begin
        exit(Ver.Major());
    end;

    procedure GetMinor(Ver: Version): Integer
    begin
        exit(Ver.Minor());
    end;

    procedure GetBuild(Ver: Version): Integer
    begin
        exit(Ver.Build());
    end;

    procedure GetRevision(Ver: Version): Integer
    begin
        exit(Ver.Revision());
    end;
}
