codeunit 84600 "CPR Src"
{
    procedure GetDisplayName(): Text
    begin
        exit(CompanyProperty.DisplayName());
    end;

    procedure GetUrlName(): Text
    begin
        exit(CompanyProperty.UrlName());
    end;

    procedure GetId(): Guid
    begin
        exit(CompanyProperty.ID());
    end;
}
