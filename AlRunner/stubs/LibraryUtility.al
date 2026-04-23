// Stub for BC's "Library - Utility" codeunit (ID 131003) from Test-TestLibraries.
// Provides common test utility methods for AL tests.
// All methods use BC built-ins so they run natively inside al-runner.
codeunit 131003 "Library - Utility"
{
    trigger OnRun()
    begin
    end;

    procedure GenerateGUID(): Text[50]
    begin
        exit(DelChr(Format(CreateGuid()), '=', '{}'));
    end;

    procedure GenerateRandomCode(FieldNo: Integer; TableNo: Integer): Code[10]
    var
        GuidTxt: Text;
    begin
        GuidTxt := DelChr(Format(CreateGuid()), '=', '{}-');
        exit(UpperCase(CopyStr(GuidTxt, 1, 10)));
    end;

    procedure GenerateRandomCode20(FieldNo: Integer; TableNo: Integer): Code[20]
    var
        GuidTxt: Text;
    begin
        GuidTxt := DelChr(Format(CreateGuid()), '=', '{}-');
        exit(UpperCase(CopyStr(GuidTxt, 1, 20)));
    end;

    procedure GenerateRandomText(MaxLength: Integer): Text
    var
        GuidTxt: Text;
    begin
        while StrLen(GuidTxt) < MaxLength do
            GuidTxt += LowerCase(DelChr(Format(CreateGuid()), '=', '{}-'));
        exit(CopyStr(GuidTxt, 1, MaxLength));
    end;

    procedure GenerateRandomXMLText(MaxLength: Integer): Text
    var
        GuidTxt: Text;
    begin
        while StrLen(GuidTxt) < MaxLength do
            GuidTxt += LowerCase(DelChr(Format(CreateGuid()), '=', '{}-'));
        exit(CopyStr(GuidTxt, 1, MaxLength));
    end;

    procedure GenerateRandomAlphabeticText(MaxLength: Integer; TextCase: Option): Text
    var
        i: Integer;
        ASCIIFrom: Integer;
        Result: Text;
    begin
        // Option 0 = Any, 1 = Upper, 2 = Lower; default to lowercase alphabetic
        ASCIIFrom := 97; // 'a'
        for i := 1 to MaxLength do
            Result[i] := ASCIIFrom + Random(25);
        if TextCase = 1 then
            exit(UpperCase(Result));
        exit(Result);
    end;
}
