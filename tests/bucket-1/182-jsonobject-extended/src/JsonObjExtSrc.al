/// Helper codeunit exercising extended JsonObject methods from issue #791.
codeunit 109000 "JOEX Src"
{
    // ── GetTime ────────────────────────────────────────────────────────────────

    procedure GetTime(Obj: JsonObject; KeyName: Text): Time
    begin
        exit(Obj.GetTime(KeyName));
    end;

    // ── GetDuration ────────────────────────────────────────────────────────────

    procedure GetDuration(Obj: JsonObject; KeyName: Text): Duration
    begin
        exit(Obj.GetDuration(KeyName));
    end;

    // ── GetOption ─────────────────────────────────────────────────────────────

    procedure GetOption(Obj: JsonObject; KeyName: Text): Integer
    begin
        exit(Obj.GetOption(KeyName));
    end;

    // ── GetByte ───────────────────────────────────────────────────────────────

    procedure GetByte(Obj: JsonObject; KeyName: Text): Byte
    begin
        exit(Obj.GetByte(KeyName));
    end;

    // ── GetBigInteger ─────────────────────────────────────────────────────────

    procedure GetBigInteger(Obj: JsonObject; KeyName: Text): BigInteger
    begin
        exit(Obj.GetBigInteger(KeyName));
    end;

    // ── Values ────────────────────────────────────────────────────────────────

    procedure ValuesCount(Obj: JsonObject): Integer
    var
        Tokens: List of [JsonToken];
    begin
        Tokens := Obj.Values();
        exit(Tokens.Count());
    end;

    // ── Path ──────────────────────────────────────────────────────────────────

    procedure PathRoot(Obj: JsonObject): Text
    begin
        exit(Obj.Path());
    end;

    // ── WriteToYaml ───────────────────────────────────────────────────────────

    procedure WriteToYamlText(Obj: JsonObject): Text
    var
        Yaml: Text;
    begin
        Obj.WriteToYaml(Yaml);
        exit(Yaml);
    end;

    // ── ReadFromYaml ──────────────────────────────────────────────────────────

    procedure ReadFromYamlGetText(YamlText: Text; KeyName: Text): Text
    var
        Obj: JsonObject;
        Tok: JsonToken;
    begin
        Obj.ReadFromYaml(YamlText);
        if Obj.Get(KeyName, Tok) then
            exit(Tok.AsValue().AsText());
        exit('<not-found>');
    end;

}
