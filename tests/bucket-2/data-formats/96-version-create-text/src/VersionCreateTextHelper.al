codeunit 1296001 "Version Create Text Helper"
{
    procedure CreateFromText(VersionText: Text): Text
    var
        Ver: Version;
    begin
        Ver := Version.Create(VersionText);
        exit(Format(Ver));
    end;

    procedure CreateAndCompareMajor(VersionText: Text): Integer
    var
        Ver: Version;
    begin
        Ver := Version.Create(VersionText);
        exit(Ver.Major());
    end;

    procedure CreateAndCompareMinor(VersionText: Text): Integer
    var
        Ver: Version;
    begin
        Ver := Version.Create(VersionText);
        exit(Ver.Minor());
    end;

    procedure CreateAndCompareBuild(VersionText: Text): Integer
    var
        Ver: Version;
    begin
        Ver := Version.Create(VersionText);
        exit(Ver.Build());
    end;

    procedure CreateAndCompareRevision(VersionText: Text): Integer
    var
        Ver: Version;
    begin
        Ver := Version.Create(VersionText);
        exit(Ver.Revision());
    end;

    procedure CompareVersions(TextA: Text; TextB: Text): Boolean
    var
        VerA: Version;
        VerB: Version;
    begin
        VerA := Version.Create(TextA);
        VerB := Version.Create(TextB);
        exit(VerA < VerB);
    end;
}
