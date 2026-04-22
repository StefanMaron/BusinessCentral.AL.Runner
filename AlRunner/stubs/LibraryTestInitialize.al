// Stub for BC's Library - Test Initialize codeunit (ID 132250).
// Provides integration event publishers for test initialization hooks.
// In real BC, test codeunits subscribe to these events to set up test data.
// In al-runner, the events fire normally through EventSubscriberRegistry.
codeunit 132250 "Library - Test Initialize"
{
    trigger OnRun()
    begin
    end;

    [IntegrationEvent(false, false)]
    procedure OnTestInitialize(CallerCodeunitID: Integer)
    begin
    end;

    [IntegrationEvent(false, false)]
    procedure OnBeforeTestSuiteInitialize(CallerCodeunitID: Integer)
    begin
    end;

    [IntegrationEvent(true, false)]
    procedure OnAfterTestSuiteInitialize(CallerCodeunitID: Integer)
    begin
    end;
}
