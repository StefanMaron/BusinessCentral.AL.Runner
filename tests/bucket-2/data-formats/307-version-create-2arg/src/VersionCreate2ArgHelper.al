codeunit 307000 "Version Create 2Arg Helper"
{
    procedure CreateMajorMinor(Major: Integer; Minor: Integer): Version
    begin
        exit(Version.Create(Major, Minor));
    end;

    procedure CreateMajorMinorBuild(Major: Integer; Minor: Integer; Build: Integer): Version
    begin
        exit(Version.Create(Major, Minor, Build));
    end;

    procedure CreateMajorMinorBuildRevision(Major: Integer; Minor: Integer; Build: Integer; Revision: Integer): Version
    begin
        exit(Version.Create(Major, Minor, Build, Revision));
    end;
}
