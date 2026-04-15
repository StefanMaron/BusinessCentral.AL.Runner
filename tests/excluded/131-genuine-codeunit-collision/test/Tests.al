// This test references the ambiguous codeunit name "Shared Helper".
// The AL compiler should emit AL0275/AL0197 for Codeunit type,
// and the runner must NOT suppress it (Codeunit is not an extension type).
codeunit 56312 "Genuine Collision Tests"
{
    Subtype = Test;
    var
        Assert: Codeunit Assert;
        Helper: Codeunit "Shared Helper";

    [Test]
    procedure SharedHelperReturnsValue()
    begin
        Assert.AreEqual('FromA', Helper.GetValue(), 'Should return value');
    end;
}
