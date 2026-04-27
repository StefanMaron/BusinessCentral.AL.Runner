/// Helper codeunit for JSON typed Add/ReadFrom/WriteTo overload tests (issue #1400).
codeunit 1316000 "Json Typed RW Src"
{
    // ── JsonArray.Add typed helpers ──────────────────────────────────────────

    procedure JsonArray_Add_Bool_Get(b: Boolean): Boolean
    var
        Arr: JsonArray;
        Token: JsonToken;
    begin
        Arr.Add(b);
        Arr.Get(0, Token);
        exit(Token.AsValue().AsBoolean());
    end;

    procedure JsonArray_Add_Int_Get(i: Integer): Integer
    var
        Arr: JsonArray;
        Token: JsonToken;
    begin
        Arr.Add(i);
        Arr.Get(0, Token);
        exit(Token.AsValue().AsInteger());
    end;

    procedure JsonArray_Add_Decimal_Get(d: Decimal): Decimal
    var
        Arr: JsonArray;
        Token: JsonToken;
    begin
        Arr.Add(d);
        Arr.Get(0, Token);
        exit(Token.AsValue().AsDecimal());
    end;

    procedure JsonArray_Add_Text_Get(t: Text): Text
    var
        Arr: JsonArray;
        Token: JsonToken;
    begin
        Arr.Add(t);
        Arr.Get(0, Token);
        exit(Token.AsValue().AsText());
    end;

    procedure JsonArray_Add_JsonObject_Get(Obj: JsonObject; PropKey: Text): Integer
    var
        Arr: JsonArray;
        Token: JsonToken;
        InnerToken: JsonToken;
    begin
        Arr.Add(Obj);
        Arr.Get(0, Token);
        Token.AsObject().Get(PropKey, InnerToken);
        exit(InnerToken.AsValue().AsInteger());
    end;

    procedure JsonArray_Add_JsonArray_Count(Inner: JsonArray): Integer
    var
        Outer: JsonArray;
        Token: JsonToken;
    begin
        Outer.Add(Inner);
        Outer.Get(0, Token);
        exit(Token.AsArray().Count());
    end;

    procedure JsonArray_Add_JsonToken_Get(Token: JsonToken): Text
    var
        Arr: JsonArray;
        Got: JsonToken;
    begin
        Arr.Add(Token);
        Arr.Get(0, Got);
        exit(Got.AsValue().AsText());
    end;

    procedure JsonArray_Add_JsonValue_Get(JVal: JsonValue): Integer
    var
        Arr: JsonArray;
        Token: JsonToken;
    begin
        Arr.Add(JVal);
        Arr.Get(0, Token);
        exit(Token.AsValue().AsInteger());
    end;

    // ── JsonArray.ReadFrom helpers ───────────────────────────────────────────

    procedure JsonArray_ReadFrom_Text_Count(Json: Text): Integer
    var
        Arr: JsonArray;
    begin
        Arr.ReadFrom(Json);
        exit(Arr.Count());
    end;

    // ── JsonObject.ReadFrom helpers ──────────────────────────────────────────

    procedure JsonObject_ReadFrom_Text_Get(Json: Text; PropKey: Text): Integer
    var
        Obj: JsonObject;
        Token: JsonToken;
    begin
        Obj.ReadFrom(Json);
        Obj.Get(PropKey, Token);
        exit(Token.AsValue().AsInteger());
    end;

    // ── JsonToken.ReadFrom helpers ───────────────────────────────────────────

    procedure JsonToken_ReadFrom_Int(Json: Text): Integer
    var
        Token: JsonToken;
    begin
        Token.ReadFrom(Json);
        exit(Token.AsValue().AsInteger());
    end;

    // ── JsonValue.ReadFrom helpers ───────────────────────────────────────────

    procedure JsonValue_ReadFrom_Int(Json: Text): Integer
    var
        JVal: JsonValue;
    begin
        JVal.ReadFrom(Json);
        exit(JVal.AsInteger());
    end;
}
