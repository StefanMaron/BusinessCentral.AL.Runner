// Issue #3515 — the external-data (Xrm / CDS / Dataverse) proxy surface is out of scope, and
// the runner refuses it by name rather than serving AL an empty registry.
//
// BaseApp's CRM/CDS Connection Setup pages call CRMIntegrationManagement.GetLastProxyVersionItem
// from OnOpenPage. That fills a `Record TempStack temporary` from the DotNet
// CrmHelper.GetProxyIdList() and then FindLasts it. GetProxyIdList returns
// XrmServiceProvider.ProxyIds — the key set of a static registry a service tier fills at
// NavEnvironment construction from DataSources/DataSources.json, one entry per proxy the
// deployment declares. A BC artifact directory is flat and ships no such file, so the runner can
// neither read that configuration nor invent it.
//
// Before this change the registry was simply empty, InitializeProxyVersionList inserted zero
// rows, and the page failed three frames below OnOpenPage on "The TempStack table is empty" —
// a BC error blaming a table that is not the problem. That is the silent-default shape
// .claude/rules/loud-failures.md forbids: the surface is out of scope
// (docs/scope.md#table-connections), so it must refuse by name.
//
// What is asserted here is runner-side: that the refusal fires on the surface AL touches, and
// that it names the API and the reason. Plain BC behaviour around it — that FindLast on a
// genuinely empty temp table errors — is BC's own and is kept as a control.
codeunit 66001 "CPV Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit "CPV Assert";

    [Test]
    procedure InitializeProxyVersionList_RefusesByName()
    var
        CRMIntegrationManagement: Codeunit "CRM Integration Management";
        TempStack: Record TempStack temporary;
    begin
        // The exact fill BaseApp performs before its FindLast. It reaches
        // CrmHelper.GetProxyIdList() through AL's DotNet interop, which is where the runner
        // refuses. The record variable is a plain temporary table and is served normally —
        // the refusal is keyed on the external-data lookup, not on TempStack.
        asserterror CRMIntegrationManagement.InitializeProxyVersionList(TempStack);

        // Naming the surface: the API slot of the refusal, not merely "something threw".
        Assert.ExpectedError('out-of-scope: CrmHelper.GetProxyIdList');
        // Naming the reason: the docs/scope.md anchor the manifest and the reader key on.
        Assert.ExpectedError('table-connections');
        // The refusal must explain WHY, so the reader is not sent looking at TempStack.
        Assert.ExpectedError('DataSources/DataSources.json');
    end;

    [Test]
    procedure GetLastProxyVersionItem_RefusesByName()
    var
        CRMIntegrationManagement: Codeunit "CRM Integration Management";
        LastProxyVersion: Integer;
    begin
        // The frame the issue's stack trace names, one level above the fill. It must carry the
        // same refusal rather than BC's "The TempStack table is empty", which is what the
        // empty registry used to produce here and which blames the wrong thing.
        asserterror LastProxyVersion := CRMIntegrationManagement.GetLastProxyVersionItem();

        Assert.ExpectedError('out-of-scope: CrmHelper.GetProxyIdList');
        Assert.ExpectedError('table-connections');

        // The old failure mode, pinned as absent: a reader seeing this message would go and
        // look at a temp table that was never the problem.
        Assert.ErrorDoesNotContain('The TempStack table is empty',
            'the refusal must name the external-data surface, not the temp table BaseApp fills from it');
    end;

    [Test]
    procedure CDSConnectionSetupPage_RefusesToOpen()
    var
        CDSConnectionSetupPage: TestPage "CDS Connection Setup";
    begin
        // The failure the issue actually reports. Page 7200's OnOpenPage calls
        // InitializeDefaultProxyVersion -> GetLastProxyVersionItem on a first open, so the
        // refusal must reach the page-open surface and not be swallowed on the way out.
        asserterror CDSConnectionSetupPage.OpenEdit();

        Assert.ExpectedError('out-of-scope: CrmHelper.GetProxyIdList');
        Assert.ExpectedError('table-connections');
    end;

    [Test]
    procedure EmptyTempStack_StillErrorsOnFindLast()
    var
        TempStack: Record TempStack temporary;
    begin
        // Scoping control, and the reason the refusal is keyed on the external-data lookup
        // rather than on anything TempStack does: a temp table nobody filled must still fail
        // its FindLast with BC's own error. If the refusal had been written over the temp
        // table instead, this would carry the out-of-scope message and the runner would be
        // hiding real empty-table bugs behind a scope boundary.
        Assert.AreEqual(0, TempStack.Count(), 'an untouched temporary TempStack holds no rows');

        asserterror TempStack.FindLast();
        Assert.IsTrue(StrPos(GetLastErrorText(), 'TempStack') > 0,
            'FindLast on a genuinely empty temp table must still raise BC''s own empty-table error.');
        Assert.ErrorDoesNotContain('out-of-scope',
            'an ordinary empty temp table is in scope and must not be refused');
    end;

    [Test]
    procedure OrdinaryTempTable_IsStillServed()
    var
        TempStack: Record TempStack temporary;
    begin
        // The other half of the scoping control: the refusal must not have made the TempStack
        // table itself unusable. Inserting and reading back is ordinary in-scope behaviour and
        // stays green, so the boundary sits on the DotNet lookup alone.
        TempStack.Init();
        TempStack.StackOrder := 7;
        TempStack.Insert();

        Assert.AreEqual(1, TempStack.Count(), 'a temporary TempStack must still accept a row');
        Assert.IsTrue(TempStack.FindLast(), 'FindLast must succeed once a row exists');
        Assert.AreEqual(7, TempStack.StackOrder, 'the row read back must be the row inserted');
    end;
}
