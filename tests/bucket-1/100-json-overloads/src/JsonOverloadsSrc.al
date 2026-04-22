/// Source helpers for JsonObject.GetText(key, bool) and JsonArray.GetObject(int) overloads.
codeunit 161000 "JSON Overloads Src"
{
    // ── JsonObject.GetText(key, requireValueExists) ────────────────────────────

    procedure GetTextWithBool(Obj: JsonObject; PropName: Text; RequireValueExists: Boolean): Text
    begin
        exit(Obj.GetText(PropName, RequireValueExists));
    end;

    // ── JsonArray.GetObject(index) ─────────────────────────────────────────────

    procedure GetObjectByIndex(Arr: JsonArray; Idx: Integer): JsonObject
    begin
        exit(Arr.GetObject(Idx));
    end;
}
