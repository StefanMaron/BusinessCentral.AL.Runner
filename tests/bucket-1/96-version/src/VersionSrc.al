/// Source codeunit exercising the Version built-in type.
codeunit 96100 "VER Source"
{
    procedure CreateVersion(Major: Integer; Minor: Integer; Build: Integer; Revision: Integer): Version
    begin
        exit(Version.Create(Major, Minor, Build, Revision));
    end;

    procedure GetMajor(Ver: Version): Integer
    begin
        exit(Ver.Major());
    end;

    procedure GetMinor(Ver: Version): Integer
    begin
        exit(Ver.Minor());
    end;

    procedure GetBuild(Ver: Version): Integer
    begin
        exit(Ver.Build());
    end;

    procedure GetRevision(Ver: Version): Integer
    begin
        exit(Ver.Revision());
    end;

    procedure GetToText(Ver: Version): Text
    begin
        exit(Ver.ToText());
    end;
}
