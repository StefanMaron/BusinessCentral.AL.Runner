/// Tests proving that Clear(rep) and Clear(xp) compile and run without error.
/// Covers issue #967: MockReportHandle and MockXmlPortHandle were missing Clear(),
/// causing CS1061 at Roslyn compile time.
codeunit 97711 "RXC Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "RXC Src";

    [Test]
    procedure ClearReport_DoesNotThrow()
    begin
        // [GIVEN] A fresh report variable
        // [WHEN]  Clear(rep) is called
        // [THEN]  No error is raised
        Src.ClearReport();
        Assert.IsTrue(true, 'Clear(rep) must not throw');
    end;

    [Test]
    procedure ClearXmlPort_DoesNotThrow()
    begin
        // [GIVEN] A fresh xmlport variable
        // [WHEN]  Clear(xp) is called
        // [THEN]  No error is raised
        Src.ClearXmlPort();
        Assert.IsTrue(true, 'Clear(xp) must not throw');
    end;

    [Test]
    procedure ClearReportTwice_DoesNotThrow()
    begin
        // [GIVEN] A report variable that is cleared twice
        // [WHEN]  Both Clear() calls complete
        // [THEN]  No error is raised
        Src.ClearReportTwice();
        Assert.IsTrue(true, 'Calling Clear(rep) twice must not throw');
    end;

    [Test]
    procedure ClearXmlPortTwice_DoesNotThrow()
    begin
        // [GIVEN] An xmlport variable that is cleared twice
        // [WHEN]  Both Clear() calls complete
        // [THEN]  No error is raised
        Src.ClearXmlPortTwice();
        Assert.IsTrue(true, 'Calling Clear(xp) twice must not throw');
    end;

    [Test]
    procedure ClearReportInline_DoesNotThrow()
    var
        rep: Report "RXC Report";
    begin
        // [GIVEN] A report variable declared inline
        // [WHEN]  Clear called directly in the test body
        Clear(rep);
        // [THEN]  No error
        Assert.IsTrue(true, 'Inline Clear(rep) must not throw');
    end;

    [Test]
    procedure ClearXmlPortInline_DoesNotThrow()
    var
        xp: XmlPort "RXC XmlPort";
    begin
        // [GIVEN] An xmlport variable declared inline
        // [WHEN]  Clear called directly in the test body
        Clear(xp);
        // [THEN]  No error
        Assert.IsTrue(true, 'Inline Clear(xp) must not throw');
    end;
}
