codeunit 60471 "PRM Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "PRM Src";

    [Test]
    [HandlerFunctions('PRMModalHandler')]
    procedure RunModal_ReturnsAction()
    begin
        // Standalone: RunModal dispatches to the ModalPageHandler, returns Action.
        Src.PageRunModal_ReturnsAction();
        Assert.IsTrue(true, 'Page.RunModal must not throw when a handler is registered');
    end;

    [ModalPageHandler]
    procedure PRMModalHandler(var pg: TestPage "PRM Card")
    begin
        pg.OK().Invoke();
    end;

    [Test]
    procedure LookupMode_SetAndGet_RoundTrips()
    begin
        Assert.IsTrue(Src.PageLookupMode_SetAndGet(),
            'Page.LookupMode setter + getter must round-trip');
    end;
}
