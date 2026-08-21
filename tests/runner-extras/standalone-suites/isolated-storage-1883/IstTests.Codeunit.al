/// #1883 follow-up — the "ALIsolatedStorage 17-hook" cluster: TenantStoragePatches.cs used
/// to JmpHook 17 overloads of ALIsolatedStorage.AL* (ALSet/ALGet/ALContains/ALDelete/
/// ALSetEncrypted, plus the internal 6-arg Set / 5-arg Get) in AlRunner/Patches/
/// TenantStoragePatches.cs, measured via `AL_RUNNER_HOOK_AUDIT=1`. JmpHook is disabled by
/// default (see AlRunner/Infrastructure/JmpHook.cs), so all 17 were silent no-ops — BC's
/// real, unpatched ALIsolatedStorage.AL* bodies ran instead.
///
/// Decompiling those bodies (Microsoft.Dynamics.Nav.Ncl.dll) showed every one of them
/// delegates entirely to IsolatedStorageRepository.Set/Get/Contains/Delete and
/// ALSystemEncryption.ALEncrypt/ALDecrypt/ALKeyExists/ALEncryptionEnabled — plus
/// GetCompanyByScope/GetUserByScope, which read NavCurrentThread.Session.Company/User.
/// Both of those lower-level targets are ALREADY Cecil-rewritten (an always-on mechanism,
/// unlike the disabled JmpHook layer) onto the exact same TenantStoragePatches in-memory
/// store and AES envelope the 17 JmpHooks would have installed — see NclCecilRewrite.cs,
/// "IsolatedStorageRepository" / "ALSystemEncryption" blocks. And Session.Company/User are
/// both seeded by BcRuntime's Cecil-owned NavSession getter cluster, independent of this
/// patch entirely. So the 17 JmpHook registrations were pure duplication of a job the lower
/// Cecil rewrite already did correctly, not a faithfulness gap — deleted outright, along with
/// their now-dead replacement bodies (ALSet_2/_3/_Secret_3, ALSetEncrypted_2/_Secret_2/_3/
/// _Secret_3, ALGet_Text_2/_3/_Secret_2/_3, ALContains_2/_3, ALDelete_1/_2, ALIsoSet_6,
/// ALIsoGet_5_Text, SetImpl/GetTextImpl/GetSecretImpl).
///
/// The upstream corpus (session/TestIsolatedStorage.al, codeunit 60378) already covers
/// Module-scope Set/Contains/Get/Delete and 2-arg SetEncrypted/Get — that coverage carries
/// over unchanged (all Module-scope calls were already routing through the same real,
/// unpatched body). This suite covers what the corpus does not reach: every other DataScope
/// value, the NavSecretText overloads, the Contains(...,var IsSecret) flag, and
/// SetEncrypted(SecretText) with an explicit non-Module scope — the surfaces most likely to
/// depend on session state this patch used to fake with a hardcoded "CRONUS"/"__skel__"
/// qualifier. Each test below passed unmodified BEFORE the JmpHook deletion too (the hooks
/// were already dead), so this is a regression guard with the narrower runner-mechanism claim
/// "no redirect fires here, and BC's real body completes correctly" — not a re-proof of the
/// underlying isolated-storage contract, which for Module scope is already proven upstream.
/// Same framing as single-site-orphaned-hooks-1883/SsohTests.Codeunit.al (see
/// bc-behavior-tests-go-upstream.md).
codeunit 62200 "IST Tests"
{
    Subtype = Test;

    // No Codeunit Assert dependency in this app (see single-site-orphaned-hooks-1883's
    // convention) — plain Error() with an explicit condition check plays the same role.

    [Test]
    procedure Company_Set_Get_RoundTrips()
    var
        V: Text;
    begin
        if not IsolatedStorage.Set('ist-co-key', 'co-value', DataScope::Company) then
            Error('Set(Company) must return true');
        if not IsolatedStorage.Get('ist-co-key', DataScope::Company, V) then
            Error('Get(Company) must return true');
        if V <> 'co-value' then
            Error('Company scope round-trip mismatch, got: %1', V);
    end;

    [Test]
    procedure User_Set_Get_RoundTrips()
    var
        V: Text;
    begin
        if not IsolatedStorage.Set('ist-user-key', 'user-value', DataScope::User) then
            Error('Set(User) must return true');
        if not IsolatedStorage.Get('ist-user-key', DataScope::User, V) then
            Error('Get(User) must return true');
        if V <> 'user-value' then
            Error('User scope round-trip mismatch, got: %1', V);
    end;

    [Test]
    procedure CompanyAndUser_Set_Get_RoundTrips()
    var
        V: Text;
    begin
        if not IsolatedStorage.Set('ist-cu-key', 'cu-value', DataScope::CompanyAndUser) then
            Error('Set(CompanyAndUser) must return true');
        if not IsolatedStorage.Get('ist-cu-key', DataScope::CompanyAndUser, V) then
            Error('Get(CompanyAndUser) must return true');
        if V <> 'cu-value' then
            Error('CompanyAndUser scope round-trip mismatch, got: %1', V);
    end;

    [Test]
    procedure Scopes_AreIsolated_SameKeyDifferentScope()
    var
        VModule: Text;
    begin
        IsolatedStorage.Set('ist-shared-key', 'module-value', DataScope::Module);
        IsolatedStorage.Set('ist-shared-key', 'company-value', DataScope::Company);
        IsolatedStorage.Get('ist-shared-key', DataScope::Module, VModule);
        if VModule <> 'module-value' then
            Error('Module-scope value must not be clobbered by a Company-scope write with the same key, got: %1', VModule);
    end;

    [Test]
    procedure SecretText_Set_Get_RoundTrips()
    var
        Secret: SecretText;
        Result: Text;
    begin
        Secret := SecretStrSubstNo('ist-sekrit-value');
        if not IsolatedStorage.Set('ist-secret-key', Secret, DataScope::Module) then
            Error('Set(SecretText) must return true');
        if not IsolatedStorage.Get('ist-secret-key', Result) then
            Error('Get(SecretText) must return true');
        if Result <> 'ist-sekrit-value' then
            Error('SecretText round-trip mismatch, got: %1', Result);
    end;

    [Test]
    procedure Contains_WithIsSecretOut_ReportsSecretFlag()
    var
        Secret: SecretText;
        IsSecret: Boolean;
    begin
        Secret := SecretStrSubstNo('ist-sekrit-value-2');
        IsolatedStorage.Set('ist-secret-key-2', Secret, DataScope::Module);
        IsolatedStorage.Set('ist-plain-key', 'plain', DataScope::Module);

        if not IsolatedStorage.Contains('ist-secret-key-2', DataScope::Module, IsSecret) then
            Error('Contains must find the secret key');
        if not IsSecret then
            Error('Contains must report IsSecret = true for a SecretText entry');

        if not IsolatedStorage.Contains('ist-plain-key', DataScope::Module, IsSecret) then
            Error('Contains must find the plain key');
        if IsSecret then
            Error('Contains must report IsSecret = false for a plain-text entry');
    end;

    [Test]
    procedure SetEncrypted_Secret_WithScope_GetRoundTrips()
    var
        Secret: SecretText;
        Result: Text;
    begin
        // 3-AL-arg SetEncrypted(Key,SecretText,DataScope) — a distinct hook (was
        // ALSetEncrypted_Secret_3) from the 2-arg form below.
        Secret := SecretStrSubstNo('ist-enc-secret-value');
        if not IsolatedStorage.SetEncrypted('ist-enc-secret-key', Secret, DataScope::Company) then
            Error('SetEncrypted(SecretText,scope) must return true');
        if not IsolatedStorage.Get('ist-enc-secret-key', DataScope::Company, Result) then
            Error('Get after SetEncrypted(SecretText,scope) must return true');
        if Result <> 'ist-enc-secret-value' then
            Error('SetEncrypted(SecretText,scope)/Get round-trip mismatch, got: %1', Result);
    end;

    [Test]
    procedure SetEncrypted_Secret_NoExplicitScope_GetRoundTrips()
    var
        Secret: SecretText;
        Result: Text;
    begin
        // 2-AL-arg SetEncrypted(Key,SecretText) — distinct hook (was
        // ALSetEncrypted_Secret_2) from the 3-arg (Key,SecretText,DataScope) form above.
        Secret := SecretStrSubstNo('ist-enc-secret-value-2');
        if not IsolatedStorage.SetEncrypted('ist-enc-secret-key-2', Secret) then
            Error('SetEncrypted(SecretText), no explicit scope, must return true');
        if not IsolatedStorage.Get('ist-enc-secret-key-2', Result) then
            Error('Get after SetEncrypted(SecretText), no explicit scope, must return true');
        if Result <> 'ist-enc-secret-value-2' then
            Error('SetEncrypted(SecretText, default scope)/Get round-trip mismatch, got: %1', Result);
    end;

    [Test]
    procedure Get_IntoSecretTextVar_ReturnsTrue_ForStoredKey()
    var
        Secret: SecretText;
        Result: SecretText;
    begin
        // ALGet_Secret_2's ByRef<NavText>-to-ByRef<NavSecretText> adapter is exercised here
        // (distinct hook from the Text-typed Get already round-trip-verified above);
        // SecretText has no equality/inspection operator available to AL test code, so the
        // provable claim here is narrower: the call succeeds and reports found=true.
        Secret := SecretStrSubstNo('ist-sekrit-value-3');
        IsolatedStorage.Set('ist-secret-key-3', Secret, DataScope::Module);
        if not IsolatedStorage.Get('ist-secret-key-3', Result) then
            Error('Get(key, var Value: SecretText) must return true for a stored key');
    end;

    [Test]
    procedure Delete_WithScope_RemovesOnlyThatScope()
    var
        VModule: Text;
    begin
        IsolatedStorage.Set('ist-del-scope-key', 'module-value', DataScope::Module);
        IsolatedStorage.Set('ist-del-scope-key', 'company-value', DataScope::Company);

        IsolatedStorage.Delete('ist-del-scope-key', DataScope::Company);

        if IsolatedStorage.Contains('ist-del-scope-key', DataScope::Company) then
            Error('Company-scope entry must be gone after a Company-scoped Delete');
        if not IsolatedStorage.Contains('ist-del-scope-key', DataScope::Module) then
            Error('Module-scope entry with the same key must survive a Company-scoped Delete');
        IsolatedStorage.Get('ist-del-scope-key', DataScope::Module, VModule);
        if VModule <> 'module-value' then
            Error('Surviving Module-scope value must be unchanged, got: %1', VModule);
    end;
}
