// #4448: "AGVI Precompiled Install Dep" subscribes to Payment Method OnAfterInsertEvent, errors
// unless it finds its own table 66360 in AllObj, and then stamps the record. This group does not
// declare that package, so the subscriber runs under a group that cannot see it: the app whose code
// is executing must still see its own closure. Meaningful only in the combined runner-extras run.
// The mechanism is docs/virtual-tables-allobj.md#precompiled-package-visibility.
codeunit 66371 "AGVS Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    var
        Assert: Codeunit "AGVS Assert";

    [Test]
    procedure PrecompiledDepSubscriber_FiredInAnUnrelatedGroup_SeesItsOwnTable()
    var
        PaymentMethod: Record "Payment Method";
    begin
        PaymentMethod.Init();
        PaymentMethod.Code := 'AGVS-SUB';
        PaymentMethod.Insert(true);
        Assert.AreEqualText('AGVI SUBSCRIBER RAN', PaymentMethod.Description,
            'the AGVI package subscriber must run to completion here, seeing its own table 66360 (meaningful only in the combined tests/runner-extras run, where app-group-visibility-install-dep registers that package)');
    end;
}
