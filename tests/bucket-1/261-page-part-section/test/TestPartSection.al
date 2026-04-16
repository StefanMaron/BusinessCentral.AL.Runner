codeunit 50264 "Test Part Section"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure PartSection_DoesNotBlockCompilation()
    var
        Helper: Codeunit "Part Page Helper";
    begin
        // A page with part/systempart sections in the same compilation unit
        // must not block compilation or prevent other codeunits from running.
        Assert.AreEqual('part section ok', Helper.GetLabel(),
            'Helper codeunit must be callable when a page with part/systempart is present');
    end;
}
