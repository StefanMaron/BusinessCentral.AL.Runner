/// Source helpers that exercise the transpiler patterns addressed in issues #1108/#1109.
///
/// The BC transpiler emits:
///   TestField(Field, NavTextVar)           → ALTestFieldSafe(fieldNo, NavType, NavText)
///   TestField(Field, BoolVar)              → ALTestFieldSafe(fieldNo, NavType, bool)
///   TestField(Field, NavTextVar, EI)       → ALTestFieldSafe(fieldNo, NavType, NavText, NavALErrorInfo)
///   TestField(Field, BoolVar, EI)          → ALTestFieldSafe(fieldNo, NavType, bool, NavALErrorInfo)
///   Find(SearchVar)                        → ALFind(DataError, NavText)  [handled via implicit NavText→string]
///
/// Additionally, some BC versions emit Find without the DataError prefix:
///   Find(SearchVar, ForceNew)              → ALFind(NavText, bool)
///
/// The new ALTestFieldSafe(object, bool) overload and ALFind(NavText, bool)/ALFind(string, bool)
/// overloads cover these patterns so CS1503 errors (NavText → DataError, bool → string) cannot occur.

table 62100 "TFNvBl Record"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; Id;   Integer)    { }
        field(2; Name; Text[100])  { }
        field(3; Flag; Boolean)    { }
        field(4; Code; Code[20])   { }
    }

    keys
    {
        key(PK; Id) { Clustered = true; }
    }
}

codeunit 62101 "TFNvBl Helper"
{
    // ── TestField(Field, TextVar) ────────────────────────────────────────────

    procedure VerifyFieldTextVar(var Rec: Record "TFNvBl Record"; ExpectedName: Text)
    begin
        Rec.TestField(Name, ExpectedName);
    end;

    // ── TestField(Field, BoolVar) ────────────────────────────────────────────

    procedure VerifyFieldBoolVar(var Rec: Record "TFNvBl Record"; ExpectedFlag: Boolean)
    begin
        Rec.TestField(Flag, ExpectedFlag);
    end;

    // ── TestField(Field, TextVar, ErrorInfo) ─────────────────────────────────

    procedure VerifyFieldTextVarEI(var Rec: Record "TFNvBl Record"; ExpectedName: Text; EI: ErrorInfo)
    begin
        Rec.TestField(Name, ExpectedName, EI);
    end;

    // ── TestField(Field, BoolVar, ErrorInfo) ─────────────────────────────────

    procedure VerifyFieldBoolVarEI(var Rec: Record "TFNvBl Record"; ExpectedFlag: Boolean; EI: ErrorInfo)
    begin
        Rec.TestField(Flag, ExpectedFlag, EI);
    end;

    // ── Find(SearchVar) — NavText search expression ──────────────────────────

    procedure FindWithTextVar(var Rec: Record "TFNvBl Record"; SearchExpr: Text): Boolean
    begin
        exit(Rec.Find(SearchExpr));
    end;
}
