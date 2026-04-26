codeunit 50262 "Test PageCustomization"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure PageCustomization_DoesNotBlockCompilation()
    var
        Helper: Codeunit "PageCustom Helper";
    begin
        // A pagecustomization object in the same compilation unit must not block
        // compilation or prevent other codeunits from running.
        Assert.AreEqual('pagecustomization ok', Helper.GetLabel(),
            'Helper codeunit must be callable when a pagecustomization is present');
    end;
}
