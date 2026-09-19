// Issue #3515 — runner-specific half of the Xrm proxy-version contract.
//
// BaseApp's CRM/CDS Connection Setup pages call CRMIntegrationManagement.GetLastProxyVersionItem,
// which fills a `Record TempStack temporary` from the DotNet CrmHelper.GetProxyIdList() and then
// FindLasts it. GetProxyIdList returns XrmServiceProvider.ProxyIds — the key set of a static
// Dictionary<int, XrmService> that BC's service tier fills at NavEnvironment construction from
// DataSources/DataSources.json. The runner's artifact directory is flat and ships no such file,
// so the registry stayed empty, InitializeProxyVersionList inserted zero rows, and the FindLast
// threw "The TempStack table is empty" three frames below OnOpenPage.
//
// What is asserted here is runner-side: that the registry BC reads is populated, and populated
// from the proxy assemblies Microsoft actually ships rather than from numbers invented here.
// The BC-behaviour half — that GetLastProxyVersionItem returns the highest registered id, and
// that TempStack.FindLast on an empty temp table errors — is proven upstream in the corpus.
codeunit 66001 "CPV Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit "CPV Assert";

    [Test]
    procedure ProxyIdList_IsNotEmpty()
    var
        CRMIntegrationManagement: Codeunit "CRM Integration Management";
        TempStack: Record TempStack temporary;
        ProxyCount: Integer;
    begin
        // The exact fill BaseApp performs before its FindLast. Before #3515 this inserted
        // zero rows, which is what made the FindLast below throw.
        CRMIntegrationManagement.InitializeProxyVersionList(TempStack);

        ProxyCount := TempStack.Count();
        Assert.IsGreaterOrEqual(ProxyCount, 1,
            'CrmHelper.GetProxyIdList() must report at least one registered Xrm proxy version; ' +
            'an empty list is what makes the CRM/CDS Connection Setup pages fail to open.');
    end;

    [Test]
    procedure GetLastProxyVersionItem_ReturnsAShippedProxyVersion()
    var
        CRMIntegrationManagement: Codeunit "CRM Integration Management";
        LastProxyVersion: Integer;
    begin
        // This is the frame the issue's stack trace names. It FindLasts the TempStack that
        // InitializeProxyVersionList filled, so it throws whenever the registry is empty.
        LastProxyVersion := CRMIntegrationManagement.GetLastProxyVersionItem();

        // The id is the major of the CRM SDK the proxy binds to, which BC itself asserts on
        // re-registration (XrmServiceProvider.RegisterXrmService). Microsoft.Xrm.Sdk.dll ships at
        // file version 9.2.x here, so the id is 9. A floor rather than an exact value keeps this
        // true when a later BC build ships a newer SDK, while still failing for the 0 that an
        // empty registry produces through a default-valued Integer.
        Assert.IsGreaterOrEqual(LastProxyVersion, 9,
            'GetLastProxyVersionItem must return a proxy version Microsoft actually ships.');
    end;

    [Test]
    procedure EveryRegisteredProxyVersion_IsAShippedSdkMajor()
    var
        CRMIntegrationManagement: Codeunit "CRM Integration Management";
        TempStack: Record TempStack temporary;
        SawNine: Boolean;
    begin
        // Discrimination control for the floor above, which a registry holding one invented
        // number would also satisfy. The registration id is the major of the CRM SDK the proxy
        // binds to — BC's own invariant — so every row must be a plausible SDK major and the
        // shipped 9.2.x SDK must appear. This fails if the registration ever starts deriving
        // ids from the proxy file names, which would give 91 and 100 instead.
        CRMIntegrationManagement.InitializeProxyVersionList(TempStack);

        Assert.IsTrue(TempStack.FindSet(), 'the proxy-version registry must not be empty');
        repeat
            Assert.IsGreaterOrEqual(TempStack.StackOrder, 1,
                'a registered proxy version must be a positive SDK major; BC rejects version <= 0.');
            if TempStack.StackOrder > 90 then
                Error('Proxy version %1 looks like a proxy FILE NAME (V91/V100), not an SDK major. ' +
                      'BC asserts id = SdkVersion.Major on re-registration.', TempStack.StackOrder);
            if TempStack.StackOrder = 9 then
                SawNine := true;
        until TempStack.Next() = 0;

        Assert.IsTrue(SawNine,
            'Microsoft.Xrm.Sdk.dll ships at file version 9.2.x, so proxy id 9 must be registered.');
    end;

    [Test]
    procedure EmptyTempStack_StillErrorsOnFindLast()
    var
        TempStack: Record TempStack temporary;
    begin
        // The negative control, and the reason this fix is a population rather than a
        // suppression: a temp table nobody filled must still fail its FindLast exactly as
        // BC does. If the fix had worked by weakening FindLast, this test would go green
        // for the wrong reason and the runner would be hiding real empty-table bugs.
        Assert.AreEqual(0, TempStack.Count(), 'an untouched temporary TempStack holds no rows');

        asserterror TempStack.FindLast();
        Assert.IsTrue(StrPos(GetLastErrorText(), 'TempStack') > 0,
            'FindLast on a genuinely empty temp table must still raise BC''s own empty-table error.');
    end;
}
