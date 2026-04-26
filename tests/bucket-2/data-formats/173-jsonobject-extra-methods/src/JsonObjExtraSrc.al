/// Helper codeunit exercising JsonObject — GetChar, GetDate, GetDateTime, WriteWithSecretsTo.
codeunit 100000 "JOE Src"
{
    procedure GetCharValue(Obj: JsonObject; KeyName: Text): Char
    begin
        exit(Obj.GetChar(KeyName));
    end;

    procedure GetDateValue(Obj: JsonObject; KeyName: Text): Date
    begin
        exit(Obj.GetDate(KeyName));
    end;

    procedure GetDateTimeValue(Obj: JsonObject; KeyName: Text): DateTime
    begin
        exit(Obj.GetDateTime(KeyName));
    end;

    procedure WriteWithSecretsToText(Obj: JsonObject): Text
    var
        Secrets: Dictionary of [Text, SecretText];
        Result: SecretText;
    begin
        Obj.WriteWithSecretsTo(Secrets, Result);
        exit(Result.Unwrap());
    end;
}
