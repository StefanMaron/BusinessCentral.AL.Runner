/// <summary>
/// #5264: each test raises an event of the app's publisher. The stubs return the default, so every
/// assertion here holds and the tests pass against them.
/// </summary>
codeunit 71960 "Lib Sub Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure RaisesEventSubscribedInLib()
    var
        Publisher: Codeunit "Lib Sub Publisher";
    begin
        Publisher.DoIt();
    end;

    [Test]
    procedure RaisesItThroughAnotherAppProcedure()
    var
        Publisher: Codeunit "Lib Sub Publisher";
    begin
        Publisher.DoItTwice();
    end;

    [Test]
    procedure RaisesEventSubscribedInTheTestBundle()
    var
        Publisher: Codeunit "Lib Sub Publisher";
    begin
        Publisher.DoTestSide();
    end;

    [Test]
    procedure RaisesAChainOfEventsAcrossThreeBundles()
    var
        Publisher: Codeunit "Lib Sub Publisher";
    begin
        Publisher.DoChain();
    end;

    [Test]
    procedure RaisesAnEventWhoseSubscriberCallsALibrary()
    var
        Publisher: Codeunit "Lib Sub Publisher";
    begin
        Publisher.DoViaLib();
    end;

    [Test]
    procedure RaisesAnEventWhoseSubscriberRaisesAnotherEventOfTheLibrary()
    var
        Publisher: Codeunit "Lib Sub Publisher";
    begin
        Publisher.RaiseY();
    end;

    [Test]
    procedure RaisesAnEventNobodySubscribesTo_IsNotAnnotated()
    var
        Publisher: Codeunit "Lib Sub Publisher";
    begin
        Publisher.Quiet();
    end;
}
