/// <summary>
/// End-to-end proof for issue #2260: `--test-data` puts the backup's own values into the
/// platform fields SystemId, SystemCreatedAt, SystemCreatedBy, SystemModifiedAt and
/// SystemModifiedBy (2000000000-2000000004), which the reader emits as `$systemId`, ….
///
/// Every value below was read out of the shipped CRONUS backup with the reader itself.
/// Customer 10000 is the subject because its SystemCreatedAt and SystemModifiedAt DIFFER, so a
/// codec that wrote one column into both fields fails; a second customer's SystemId catches a
/// value copied across rows. Before the fix every one of these read the field's default
/// (a null GUID, a blank DateTime).
///
/// NOT RUN BY CI — see README.md in this directory.
/// </summary>
codeunit 64410 "Test Data System Fields Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit "TDF Assert";

    [Test]
    procedure CustomerSystemIdIsTheBackupsValue()
    var
        Customer: Record Customer;
    begin
        Assert.IsTrue(Customer.Get('10000'), 'Customer 10000 must exist after --test-data hydration');
        Assert.AreEqual('{E89EC9E1-D953-F111-8E26-7CED8D9E4094}', Format(Customer.SystemId), 'Customer 10000 SystemId');

        Assert.IsTrue(Customer.Get('20000'), 'Customer 20000 must exist after --test-data hydration');
        Assert.AreEqual('{F09EC9E1-D953-F111-8E26-7CED8D9E4094}', Format(Customer.SystemId), 'Customer 20000 SystemId');
    end;

    [Test]
    procedure CustomerAuditFieldsAreTheBackupsValues()
    var
        Customer: Record Customer;
    begin
        Customer.Get('10000');
        // Format 9 renders the instant in UTC, which is how BC stores these columns.
        Assert.AreEqual('2026-05-19T23:24:33.903Z', Format(Customer.SystemCreatedAt, 0, 9), 'Customer 10000 SystemCreatedAt');
        Assert.AreEqual('2026-05-19T23:24:34.417Z', Format(Customer.SystemModifiedAt, 0, 9), 'Customer 10000 SystemModifiedAt');
        Assert.AreEqual('{00000000-0000-0000-0000-000000000001}', Format(Customer.SystemCreatedBy), 'Customer 10000 SystemCreatedBy');
        Assert.AreEqual('{00000000-0000-0000-0000-000000000001}', Format(Customer.SystemModifiedBy), 'Customer 10000 SystemModifiedBy');
    end;

    [Test]
    procedure GetBySystemIdFindsAHydratedRow()
    var
        Customer: Record Customer;
    begin
        Assert.IsTrue(Customer.GetBySystemId('{E89EC9E1-D953-F111-8E26-7CED8D9E4094}'),
            'GetBySystemId must find the hydrated Customer 10000 by the SystemId the backup holds');
        Assert.AreEqual('10000', Customer."No.", 'GetBySystemId must return Customer 10000');
    end;

    [Test]
    procedure GetBySystemIdDoesNotFindAnIdTheBackupLacks()
    var
        Customer: Record Customer;
    begin
        Assert.IsFalse(Customer.GetBySystemId('{11111111-2222-3333-4444-555555555555}'),
            'a SystemId no row in the backup holds must not be found');
    end;

    [Test]
    procedure NoSeriesSystemIdIsTheBackupsValue()
    var
        NoSeries: Record "No. Series";
    begin
        Assert.IsTrue(NoSeries.Get('A-BLK'), 'No. Series A-BLK must exist after --test-data hydration');
        Assert.AreEqual('{C749D1DB-D953-F111-8E26-7CED8D9E4094}', Format(NoSeries.SystemId), 'A-BLK SystemId');
        // The backup holds 23:24:22.700; format 9 drops trailing zeros from the milliseconds.
        Assert.AreEqual('2026-05-19T23:24:22.7Z', Format(NoSeries.SystemCreatedAt, 0, 9), 'A-BLK SystemCreatedAt');
    end;
}
