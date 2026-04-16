/// Helper codeunit exercising ModuleInfo property access in standalone mode.
codeunit 84200 "MI Src"
{
    /// Returns AppVersion from a default-initialised ModuleInfo.
    procedure DefaultAppVersion(): Text
    var
        Info: ModuleInfo;
        Ver: Version;
    begin
        Ver := Info.AppVersion;
        exit(Format(Ver));
    end;

    /// Returns DataVersion from a default-initialised ModuleInfo.
    procedure DefaultDataVersion(): Text
    var
        Info: ModuleInfo;
        Ver: Version;
    begin
        Ver := Info.DataVersion;
        exit(Format(Ver));
    end;

    /// Returns Id (GUID) from a default-initialised ModuleInfo.
    /// Braces are stripped so the result is stable across BC versions
    /// (16.2 returns "00000000-..." while 26+ returns "{00000000-...}").
    procedure DefaultId(): Text
    var
        Info: ModuleInfo;
    begin
        exit(DelChr(Format(Info.Id), '=', '{}'));
    end;

    /// Returns PackageId (GUID) from a default-initialised ModuleInfo.
    /// Braces are stripped — see DefaultId for the cross-version rationale.
    procedure DefaultPackageId(): Text
    var
        Info: ModuleInfo;
    begin
        exit(DelChr(Format(Info.PackageId), '=', '{}'));
    end;

    /// Returns Name from a default-initialised ModuleInfo.
    procedure DefaultName(): Text
    var
        Info: ModuleInfo;
    begin
        exit(Info.Name);
    end;

    /// Returns Publisher from a default-initialised ModuleInfo.
    procedure DefaultPublisher(): Text
    var
        Info: ModuleInfo;
    begin
        exit(Info.Publisher);
    end;

    /// Returns the count of Dependencies from a default-initialised ModuleInfo.
    procedure DefaultDependencyCount(): Integer
    var
        Info: ModuleInfo;
        Deps: List of [ModuleDependencyInfo];
    begin
        Deps := Info.Dependencies;
        exit(Deps.Count);
    end;
}
