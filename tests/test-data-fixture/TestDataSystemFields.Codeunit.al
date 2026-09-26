/// <summary>
/// End-to-end proof for issue #2260: `--test-data` puts the backup's own values into the
/// platform fields SystemId, SystemCreatedAt, SystemCreatedBy, SystemModifiedAt and
/// SystemModifiedBy (2000000000-2000000004), which the reader emits as `$systemId`, ….
///
/// NO LITERAL SYSTEMID OR INSTANT. Both are minted when the artifact's demo data is built, so
/// every backup carries different ones (#4645). What every W1 backup shares, and what these
/// tests assert instead:
///   - SQL Server fills SystemId from NEWSEQUENTIALID(), whose last eight bytes (clock
///     sequence and node) are the same for every row one server minted. So two rows of one
///     backup — of any table — agree on them, and a codec that minted fresh GUIDs does not.
///   - Customer 10000's SystemCreatedAt is earlier than its SystemModifiedAt, and earlier than
///     Customer 20000's, so a codec sending one column to both fields, or copying a value
///     across rows, fails.
///   - SystemCreatedBy / SystemModifiedBy are the demo-data user {…-000000000001}.
/// Before the fix every one of these read the field's default (a null GUID, a blank DateTime).
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
        OtherCustomer: Record Customer;
    begin
        Assert.IsTrue(Customer.Get('10000'), 'Customer 10000 must exist after --test-data hydration');
        Assert.IsTrue(OtherCustomer.Get('20000'), 'Customer 20000 must exist after --test-data hydration');
        Assert.IsFalse(IsNullGuid(Customer.SystemId), 'Customer 10000 SystemId must be the backup''s, not blank');
        Assert.IsFalse(Customer.SystemId = OtherCustomer.SystemId, 'two customers must not share a SystemId');
        Assert.AreEqual(SequentialNode(Customer.SystemId), SequentialNode(OtherCustomer.SystemId),
            'SystemIds one SQL Server minted share their NEWSEQUENTIALID node bytes; a mismatch means the '
            + 'runner minted at least one of them instead of reading the backup');
    end;

    [Test]
    procedure CustomerAuditFieldsAreTheBackupsValues()
    var
        Customer: Record Customer;
        OtherCustomer: Record Customer;
    begin
        Customer.Get('10000');
        OtherCustomer.Get('20000');
        Assert.IsFalse(Customer.SystemCreatedAt = 0DT, 'Customer 10000 SystemCreatedAt must be the backup''s, not blank');
        Assert.IsTrue(Customer.SystemCreatedAt < Customer.SystemModifiedAt,
            StrSubstNo('Customer 10000 was modified after it was created in every W1 backup; got created %1, modified %2',
                Format(Customer.SystemCreatedAt, 0, 9), Format(Customer.SystemModifiedAt, 0, 9)));
        Assert.IsTrue(Customer.SystemCreatedAt < OtherCustomer.SystemCreatedAt,
            StrSubstNo('Customer 10000 was created before Customer 20000; got %1 and %2',
                Format(Customer.SystemCreatedAt, 0, 9), Format(OtherCustomer.SystemCreatedAt, 0, 9)));
        Assert.IsTrue(Customer.SystemCreatedAt < CurrentDateTime(),
            'a restored SystemCreatedAt predates this run; one at or after it was stamped by the runner');
        Assert.AreEqual('{00000000-0000-0000-0000-000000000001}', Format(Customer.SystemCreatedBy), 'Customer 10000 SystemCreatedBy');
        Assert.AreEqual('{00000000-0000-0000-0000-000000000001}', Format(Customer.SystemModifiedBy), 'Customer 10000 SystemModifiedBy');
    end;

    [Test]
    procedure GetBySystemIdFindsAHydratedRow()
    var
        Customer: Record Customer;
        CustomerSystemId: Guid;
    begin
        Customer.Get('10000');
        CustomerSystemId := Customer.SystemId;
        Assert.IsFalse(IsNullGuid(CustomerSystemId), 'Customer 10000 SystemId must be the backup''s, not blank');
        Clear(Customer);
        Assert.IsTrue(Customer.GetBySystemId(CustomerSystemId),
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
    procedure RestoredRowVersionIsNonZeroAndOutrankedByALaterWrite()
    var
        NoSeries: Record "No. Series";
        OtherNoSeries: Record "No. Series";
        RestoredRowVersion: BigInteger;
        OtherRowVersion: BigInteger;
        WrittenRowVersion: BigInteger;
    begin
        // #4123. Deliberately NOT asserting the literal rowversion: it is demo-data build state
        // that moves with the artifact, exactly as TestDataDateValues says of its instant.
        //
        // NOTE on what these assertions are worth. `RestoredRowVersion > 0` and
        // `Written > Restored` BOTH pass on the unfixed runner, because #1980's stamp writes a
        // non-zero value on every insert and Interlocked.Increment is monotonic. They are
        // regression cover, not proof of this fix. What discriminates is the THIRD assertion:
        // two restored rows must keep the backup's relative order, which per-row stamping
        // destroys (measured: 261652, 100, 500000 became 261653, 261654, 500001).
        Assert.IsTrue(NoSeries.Get('A-BLK'), 'No. Series A-BLK must exist after --test-data hydration');
        RestoredRowVersion := NoSeries.SystemRowVersion;
        Assert.IsTrue(RestoredRowVersion > 0,
            'a restored row must carry the backup rowversion, not field 0 default (#4123)');

        // Two restored rows, compared against each other. Under per-row stamping the value AL
        // sees is a function of hydration order rather than of backup state, so this is the
        // assertion that fails on the unfixed path. The SECOND row is whichever one follows in
        // the table -- not a hardcoded code, because which codes the shipped backup carries is
        // artifact state this test must not guess at.
        OtherNoSeries.SetFilter(Code, '<>%1', 'A-BLK');
        Assert.IsTrue(OtherNoSeries.FindFirst(),
            'the backup must hold a second No. Series row to compare rowversions against');
        OtherRowVersion := OtherNoSeries.SystemRowVersion;
        Assert.AreNotEqual(RestoredRowVersion, OtherRowVersion,
            'two restored rows must not share a rowversion: SQL assigns each row its own, and '
            + 'equal values here mean the stamp overwrote both with consecutive counter values');

        // A row written now must outrank both. Before seeding, the first stamp is 1 against a
        // restored value in the hundreds of thousands.
        NoSeries.Init();
        NoSeries.Code := 'TDF-RV-1';
        NoSeries.Description := 'rowversion ordering probe';
        NoSeries.Insert();
        WrittenRowVersion := NoSeries.SystemRowVersion;

        Assert.IsTrue(WrittenRowVersion > RestoredRowVersion,
            'a row inserted after the restore must sort AFTER every restored row; real SQL has '
            + 'one monotonic sequence per database, and an unseeded counter gives the runner two');
        Assert.IsTrue(WrittenRowVersion > OtherRowVersion,
            'the later write must outrank EVERY restored row, not only the first one read');
    end;

    [Test]
    procedure NoSeriesSystemIdIsTheBackupsValue()
    var
        NoSeries: Record "No. Series";
        Customer: Record Customer;
    begin
        Assert.IsTrue(NoSeries.Get('A-BLK'), 'No. Series A-BLK must exist after --test-data hydration');
        Assert.IsTrue(Customer.Get('10000'), 'Customer 10000 must exist after --test-data hydration');
        Assert.IsFalse(IsNullGuid(NoSeries.SystemId), 'A-BLK SystemId must be the backup''s, not blank');
        // Another TABLE's row, so this also catches a per-table mapping error the Customer test cannot.
        Assert.AreEqual(SequentialNode(Customer.SystemId), SequentialNode(NoSeries.SystemId),
            'A-BLK and Customer 10000 come from one database, so their SystemIds share the NEWSEQUENTIALID node');
        // Demo data sets up number series before it creates customers.
        Assert.IsFalse(NoSeries.SystemCreatedAt = 0DT, 'A-BLK SystemCreatedAt must be the backup''s, not blank');
        Assert.IsTrue(NoSeries.SystemCreatedAt < Customer.SystemCreatedAt,
            StrSubstNo('A-BLK was created before Customer 10000; got %1 and %2',
                Format(NoSeries.SystemCreatedAt, 0, 9), Format(Customer.SystemCreatedAt, 0, 9)));
    end;

    /// <summary>The trailing eight bytes of a NEWSEQUENTIALID GUID: clock sequence and node, the
    /// same for every id one SQL Server instance mints.</summary>
    local procedure SequentialNode(Id: Guid): Text
    var
        Formatted: Text;
    begin
        Formatted := UpperCase(DelChr(Format(Id), '=', '{}'));
        exit(CopyStr(Formatted, StrLen(Formatted) - 16));
    end;
}
