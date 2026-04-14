codeunit 50860 "Global Language Api"
{
    procedure GetCurrentLanguage(): Integer
    begin
        exit(GlobalLanguage());
    end;

    procedure SetAndRestoreLanguage(): Integer
    var
        Original: Integer;
    begin
        Original := GlobalLanguage();
        GlobalLanguage(1033);
        GlobalLanguage(Original);
        exit(GlobalLanguage());
    end;

    procedure SetLanguage(LanguageId: Integer)
    begin
        GlobalLanguage(LanguageId);
    end;

    procedure GetLanguageAfterSet(LanguageId: Integer): Integer
    begin
        GlobalLanguage(LanguageId);
        exit(GlobalLanguage());
    end;
}
