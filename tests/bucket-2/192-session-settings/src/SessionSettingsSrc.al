/// Helper codeunit exercising SessionSettings — Init + property
/// accessors + RequestSessionUpdate. Standalone semantics: settings
/// hold in-memory defaults; RequestSessionUpdate is a no-op (no
/// service-tier session to refresh).
codeunit 60180 "SST Src"
{
    procedure Init_DoesNotThrow(): Boolean
    var
        s: SessionSettings;
    begin
        s.Init();
        exit(true);
    end;

    procedure GetCompany(): Text
    var
        s: SessionSettings;
    begin
        s.Init();
        exit(s.Company);
    end;

    procedure SetAndGetCompany(value: Text): Text
    var
        s: SessionSettings;
    begin
        s.Init();
        s.Company := value;
        exit(s.Company);
    end;

    procedure SetAndGetLanguageId(value: Integer): Integer
    var
        s: SessionSettings;
    begin
        s.Init();
        s.LanguageId := value;
        exit(s.LanguageId);
    end;

    procedure SetAndGetLocaleId(value: Integer): Integer
    var
        s: SessionSettings;
    begin
        s.Init();
        s.LocaleId := value;
        exit(s.LocaleId);
    end;

    procedure SetAndGetTimeZone(value: Text): Text
    var
        s: SessionSettings;
    begin
        s.Init();
        s.TimeZone := value;
        exit(s.TimeZone);
    end;

    procedure SetAndGetProfileId(value: Text): Text
    var
        s: SessionSettings;
    begin
        s.Init();
        s.ProfileId := value;
        exit(s.ProfileId);
    end;

    procedure RequestSessionUpdate_NoOp(): Boolean
    var
        s: SessionSettings;
    begin
        s.Init();
        s.Company := 'Acme';
        s.RequestSessionUpdate(false);
        // After the update request the in-memory value stays intact — nothing
        // to refresh standalone.
        exit(s.Company = 'Acme');
    end;
}
