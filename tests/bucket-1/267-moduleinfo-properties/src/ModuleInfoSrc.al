/// Helper codeunit exercising ModuleInfo property access in standalone mode.
codeunit 84100 "MI Src"
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
    procedure DefaultId(): Text
    var
        Info: ModuleInfo;
    begin
        exit(Format(Info.Id));
    end;

    /// Returns PackageId (GUID) from a default-initialised ModuleInfo.
    procedure DefaultPackageId(): Text
    var
        Info: ModuleInfo;
    begin
        exit(Format(Info.PackageId));
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
        Deps: List of [ModuleInfo];
    begin
        Deps := Info.Dependencies;
        exit(Deps.Count);
    end;
}
