/// Helper codeunit exercising JsonObject methods: Add/Get/Contains/Keys/Remove/Replace/Clone/AsToken/Path and typed getters.
codeunit 84200 "JOM Src"
{
    /// Add two properties and Get them back as a round-trip.
    procedure AddAndGet_Text(): Text
    var
        JObj: JsonObject;
        JTok: JsonToken;
    begin
        JObj.Add('name', 'runner');
        if JObj.Get('name', JTok) then
            exit(JTok.AsValue().AsText());
        exit('<not-found>');
    end;

    /// Add an integer property and Get it back.
    procedure AddAndGet_Integer(): Integer
    var
        JObj: JsonObject;
        JTok: JsonToken;
    begin
        JObj.Add('count', 42);
        if JObj.Get('count', JTok) then
            exit(JTok.AsValue().AsInteger());
        exit(-1);
    end;

    /// Contains returns true for an existing key.
    procedure Contains_Existing(): Boolean
    var
        JObj: JsonObject;
    begin
        JObj.Add('present', true);
        exit(JObj.Contains('present'));
    end;

    /// Contains returns false for a missing key.
    procedure Contains_Missing(): Boolean
    var
        JObj: JsonObject;
    begin
        exit(JObj.Contains('absent'));
    end;

    /// Keys returns the list of property names.
    procedure Keys_Count(): Integer
    var
        JObj: JsonObject;
        K: List of [Text];
    begin
        JObj.Add('a', 1);
        JObj.Add('b', 2);
        JObj.Add('c', 3);
        K := JObj.Keys();
        exit(K.Count);
    end;

    /// Remove deletes a property; Contains returns false afterwards.
    procedure Remove_Key(): Boolean
    var
        JObj: JsonObject;
    begin
        JObj.Add('temp', 'x');
        JObj.Remove('temp');
        exit(JObj.Contains('temp'));
    end;

    /// Replace swaps an existing value.
    procedure Replace_Value(): Text
    var
        JObj: JsonObject;
        NewTok: JsonToken;
        JTok: JsonToken;
    begin
        JObj.Add('status', 'old');
        NewTok.ReadFromText('"new"');
        JObj.Replace('status', NewTok);
        if JObj.Get('status', JTok) then
            exit(JTok.AsValue().AsText());
        exit('<not-found>');
    end;

    /// Clone produces an independent copy.
    procedure Clone_IsIndependent(): Boolean
    var
        JObj: JsonObject;
        JTok2: JsonToken;
        Cloned: JsonToken;
        Sub: JsonObject;
    begin
        JObj.Add('x', 1);
        Cloned := JObj.AsToken().Clone();
        // Modify original after cloning
        JObj.Add('y', 2);
        // Clone should still have only 1 key
        Sub := Cloned.AsObject();
        exit(Sub.Keys().Count = 1);
    end;

    /// AsToken wraps the object in a JsonToken.
    procedure AsToken_IsObject(): Boolean
    var
        JObj: JsonObject;
        JTok: JsonToken;
    begin
        JObj.Add('k', 'v');
        JTok := JObj.AsToken();
        exit(JTok.IsObject());
    end;

    /// GetText returns the text value for a string property.
    procedure GetText_Key(): Text
    var
        JObj: JsonObject;
    begin
        JObj.Add('msg', 'hello');
        exit(JObj.GetText('msg'));
    end;

    /// GetInteger returns the integer value for an integer property.
    procedure GetInteger_Key(): Integer
    var
        JObj: JsonObject;
    begin
        JObj.Add('num', 99);
        exit(JObj.GetInteger('num'));
    end;

    /// GetDecimal returns the decimal value for a decimal property.
    procedure GetDecimal_Key(): Decimal
    var
        JObj: JsonObject;
    begin
        JObj.Add('price', 3.14);
        exit(JObj.GetDecimal('price'));
    end;

    /// GetObject returns a nested JsonObject.
    procedure GetObject_Key(): Boolean
    var
        JObj: JsonObject;
        Inner: JsonObject;
        Sub: JsonObject;
    begin
        Inner.Add('nested', true);
        JObj.Add('obj', Inner);
        Sub := JObj.GetObject('obj');
        exit(Sub.Contains('nested'));
    end;

    /// Get returns false for a missing key.
    procedure Get_Missing(): Boolean
    var
        JObj: JsonObject;
        JTok: JsonToken;
    begin
        exit(JObj.Get('nope', JTok));
    end;
}
