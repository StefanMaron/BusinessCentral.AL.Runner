/// Source codeunit exercising JsonArray.Clone() and JsonArray.AsToken().
codeunit 62010 "JAC Src"
{
    /// Clone an array and return the clone as a JsonToken.
    procedure CloneArray(Arr: JsonArray): JsonToken
    begin
        exit(Arr.Clone());
    end;

    /// Verify Clone produces an independent deep copy:
    /// mutate the original and confirm the clone is unchanged.
    procedure CloneIsIndependent(Arr: JsonArray): Integer
    var
        Clone: JsonToken;
        CloneArr: JsonArray;
    begin
        Clone := Arr.Clone();
        CloneArr := Clone.AsArray();
        // Mutate original — clone must not see the new element
        Arr.Add(999);
        exit(CloneArr.Count());
    end;

    /// Convert a JsonArray to a JsonToken via AsToken().
    procedure ArrayAsToken(Arr: JsonArray): JsonToken
    begin
        exit(Arr.AsToken());
    end;

    /// Confirm that AsToken() returns a token that IsArray() = true.
    procedure AsTokenIsArray(Arr: JsonArray): Boolean
    var
        Token: JsonToken;
    begin
        Token := Arr.AsToken();
        exit(Token.IsArray());
    end;

    /// Confirm that after AsToken the array content is accessible.
    procedure AsTokenCount(Arr: JsonArray): Integer
    var
        Token: JsonToken;
        A: JsonArray;
    begin
        Token := Arr.AsToken();
        A := Token.AsArray();
        exit(A.Count());
    end;
}
