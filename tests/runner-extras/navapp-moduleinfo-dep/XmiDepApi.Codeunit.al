/// <summary>
/// Reports this DEPENDENCY app's own module identity — the SPBLIC
/// CheckSupportedVersion pattern (NavApp.GetCurrentModuleInfo inside a dep must
/// see the dep's version, not the consuming bundle's).
/// </summary>
codeunit 61230 "XMI Dep Api"
{
    procedure OwnVersion(): Text
    var
        Info: ModuleInfo;
    begin
        NavApp.GetCurrentModuleInfo(Info);
        exit(Format(Info.AppVersion()));
    end;

    procedure OwnName(): Text
    var
        Info: ModuleInfo;
    begin
        NavApp.GetCurrentModuleInfo(Info);
        exit(Info.Name());
    end;

    procedure CallerName(): Text
    var
        Info: ModuleInfo;
    begin
        NavApp.GetCallerModuleInfo(Info);
        exit(Info.Name());
    end;

    /// <summary>
    /// The bundle calls this, and this hops through a SECOND codeunit of this same dep
    /// before asking for the caller. BC's ALGetCallerModuleInfo skips exactly ONE method
    /// scope and then takes the next stack frame, so the answer is the module of the
    /// IMMEDIATE caller — this dep — not the nearest frame from a different app.
    /// </summary>
    procedure CallerNameAfterOwnHop(): Text
    var
        Hop: Codeunit "XMI Dep Hop";
    begin
        exit(Hop.CallerNameSeenFromHere());
    end;

    /// <summary>Same hop, reporting the caller's AppId so an empty GUID is visible.</summary>
    procedure CallerIdAfterOwnHop(): Guid
    var
        Hop: Codeunit "XMI Dep Hop";
    begin
        exit(Hop.CallerIdSeenFromHere());
    end;

    procedure OwnId(): Guid
    var
        Info: ModuleInfo;
    begin
        NavApp.GetCurrentModuleInfo(Info);
        exit(Info.Id());
    end;

    /// <summary>
    /// #1942 regression: the source polyfill behind NavApp.GetCurrentModuleInfo used to
    /// be declared `void`, so any caller using the return value in a boolean CONTEXT
    /// (`if not NavApp.GetCurrentModuleInfo(Info) then`) failed to COMPILE (CS0023 on the
    /// generated C#) rather than misbehaving at run time. Every other procedure in this
    /// codeunit uses the statement form, which never hit the bug. Reports this dep's own
    /// identity through the boolean form, so it discriminates against the bundle's.
    /// </summary>
    procedure OwnVersionBooleanForm(): Text
    var
        Info: ModuleInfo;
    begin
        if not NavApp.GetCurrentModuleInfo(Info) then
            exit('');
        exit(Format(Info.AppVersion()));
    end;

    procedure OwnNameBooleanForm(): Text
    var
        Info: ModuleInfo;
    begin
        if not NavApp.GetCurrentModuleInfo(Info) then
            exit('');
        exit(Info.Name());
    end;

    procedure OwnIdBooleanForm(): Guid
    var
        Info: ModuleInfo;
    begin
        if not NavApp.GetCurrentModuleInfo(Info) then
            exit;
        exit(Info.Id());
    end;

    /// <summary>Boolean-form coverage for GetCallerModuleInfo -- its polyfill already
    /// returned bool before #1942, so this is regression cover for a value context that
    /// was never previously exercised, not part of the fix itself.</summary>
    procedure CallerNameBooleanForm(): Text
    var
        Info: ModuleInfo;
    begin
        if not NavApp.GetCallerModuleInfo(Info) then
            exit('');
        exit(Info.Name());
    end;
}

/// <summary>
/// A second codeunit in the SAME app as <see cref="XMI Dep Api"/>. Its only purpose is to
/// put one more same-app method scope between the bundle and the GetCallerModuleInfo call.
/// </summary>
codeunit 61231 "XMI Dep Hop"
{
    procedure CallerNameSeenFromHere(): Text
    var
        Info: ModuleInfo;
    begin
        NavApp.GetCallerModuleInfo(Info);
        exit(Info.Name());
    end;

    procedure CallerIdSeenFromHere(): Guid
    var
        Info: ModuleInfo;
    begin
        NavApp.GetCallerModuleInfo(Info);
        exit(Info.Id());
    end;
}
