/// Regression for issue #2487: NavSession.DocumentStorageService was never assigned on the
/// skeleton session, so every codeunit-9510 call that touched it -- starting with
/// SetServiceType -- NREd inside Microsoft.Dynamics.Nav.NavDocumentService.NavDocumentServiceHelper
/// .SetDocumentServiceType before the AL business logic under test ever ran.
///
/// This suite deliberately does NOT re-assert what codeunit 9510's SetServiceType/GetServiceType
/// or TestConnection *mean* -- that is Microsoft's own Base App code, exercised faithfully here,
/// and its behaviour is not owned by this repo (see bc-behavior-tests-go-upstream.md). What this
/// suite pins is the runner-internal claim: the property is seeded so the surface no longer NREs,
/// and where the runner genuinely cannot go further (no DOCUMENTSERVICEMOCK handler DLL shipped
/// with the platform artifacts this runner provisions), it fails with BC's own named exception --
/// never a bare NullReferenceException (loud-failures.md).
codeunit 65511 "Dss Tests"
{
    Subtype = Test;

    [Test]
    procedure SetServiceType_GetServiceType_RoundTrips()
    var
        DocumentServiceMgt: Codeunit "Document Service Management";
        Assert: Codeunit "Dss Assert";
        ActualServiceType: Text;
    begin
        // [SCENARIO] SetServiceType stores the requested type on NavSession.DocumentStorageService
        // via NavDocumentServiceHelper.SetDocumentServiceType. Before the fix this NREd on first
        // call because the property was never assigned.

        // [GIVEN] no service type has been requested yet on this session.
        // [WHEN] SetServiceType is called.
        DocumentServiceMgt.SetServiceType('DOCUMENTSERVICEMOCK');

        // [THEN] GetServiceType reflects it -- proving the call reached codeunit 9510's AL body
        // and returned, instead of the runner's skeleton session NRE-ing inside the DotNet call.
        ActualServiceType := DocumentServiceMgt.GetServiceType();
        Assert.AreEqual('DOCUMENTSERVICEMOCK', ActualServiceType,
            'SetServiceType/GetServiceType must round-trip once NavSession.DocumentStorageService is seeded');

        // [WHEN] the service type is changed.
        DocumentServiceMgt.SetServiceType('EMPTYDOCUMENTSERVICEMOCK');

        // [THEN] the second call also succeeds and the new value round-trips -- proving the seed
        // is not a one-shot workaround that only survives a single call.
        ActualServiceType := DocumentServiceMgt.GetServiceType();
        Assert.AreEqual('EMPTYDOCUMENTSERVICEMOCK', ActualServiceType,
            'a second SetServiceType call must also round-trip without NRE-ing');
    end;

    [Test]
    procedure UnresolvableHandler_FailsWithNamedBcException_NotNRE()
    var
        DocumentServiceConfiguration: Record "Document Service";
        DocumentServiceMgt: Codeunit "Document Service Management";
        Assert: Codeunit "Dss Assert";
    begin
        // [SCENARIO] Once NavSession.DocumentStorageService is seeded, SetServiceType no longer
        // NREs, so codeunit 9510 proceeds into the real document-service-handler resolution path
        // (Microsoft.Dynamics.Nav.DocumentService.DocumentServiceFactory, an MEF DirectoryCatalog
        // scan of the platform install folder for the requested handler). The platform artifacts
        // this runner provisions do not ship the DOCUMENTSERVICEMOCK test-toolkit handler, so the
        // real BC factory itself raises its own "provider not found" exception. That must surface
        // as BC's own named, diagnosed message -- never as a bare NullReferenceException.

        // [GIVEN] a document service configuration exists, so TestConnection reaches the handler
        // resolution step instead of failing earlier on "no configuration".
        DocumentServiceConfiguration.Init();
        DocumentServiceConfiguration."Service ID" := 'SO1';
        DocumentServiceConfiguration.Description := 'Dss Test Service';
        DocumentServiceConfiguration.Location := 'http://ValidLocation';
        DocumentServiceConfiguration."User Name" := 'a@b.c';
        DocumentServiceConfiguration.Password := 'pwd';
        DocumentServiceConfiguration."Document Repository" := 'Documents';
        DocumentServiceConfiguration.Folder := 'TempFolder';
        DocumentServiceConfiguration.Insert(true);

        DocumentServiceMgt.SetServiceType('DOCUMENTSERVICEMOCK');

        // [WHEN] TestConnection is called.
        asserterror DocumentServiceMgt.TestConnection();

        // [THEN] the error names the real BC API and the missing handler -- not "Object reference
        // not set to an instance of an object", which is what NavSession.DocumentStorageService
        // being null used to produce.
        Assert.ExpectedError('DocumentServiceFactory.CreateService');
        Assert.ExpectedError('DOCUMENTSERVICEMOCK');
        Assert.ErrorDoesNotContain('Object reference not set');
    end;
}
