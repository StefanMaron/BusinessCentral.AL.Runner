/// <summary>
/// End-to-end proof for issue #2270 and the DB-NULL half of #2268: `--test-data` rebuilds
/// Blob, Media, MediaSet and RecordId values out of the backup, and AL reads them back as the
/// values BC stored.
///
/// THE ASSERTION THAT MATTERS IS THE BLOB'S CONTENT. A BC Blob column does not hold the
/// field's bytes; it holds BC's container — four magic bytes (02 45 7D 5B) followed by a raw
/// Deflate stream — whenever the field is Compressed. A codec that stored the container
/// verbatim would still produce a record that exists, a blob with `HasValue` = true and a
/// non-zero length, so every assertion short of comparing the CONTENT would pass with the bug
/// present. `BlobContentIsTheDecompressedValue` reads the text out through an InStream and
/// compares it.
///
/// THE SUBJECTS, AND WHY THESE
///   - `Retention Policy Setup Line` is the only table in the shipped demo data whose Blob
///     holds readable text, so it is the one Blob whose real value AL can state exactly
///     rather than by length. 46 stored bytes decompress to 47.
///   - `Sales Header` is the table #2268 named. It refused over a NULL `Work Description` —
///     nothing about its own data — and it is the table a posting test needs. Both halves are
///     asserted: a real field's value (the table is really there) and the NULL blob reading
///     back as no value (the NULL is really a NULL).
///   - `Word Template` and `Customer` cover Media, `Item Variant` covers MediaSet. All three
///     assert the stored id, which is the whole of what BC's row read puts in the record —
///     the bytes behind an id live in Tenant Media, a table like any other.
///   - `Bank Account` covers RecordId, whose CRONUS value is blank. Paired with a real field
///     on the same record so "the table hydrated" is proved separately from "the RecordId is
///     blank", which a refused table would also (vacuously) satisfy.
///
/// ON BC VERSIONS. This bundle is not run by CI, but it is run by hand against more than one
/// artifact, and demo data is not guaranteed identical across them. Every concrete id below is
/// paired with an assertion that does not depend on the exact value — a present record, a
/// non-blank id, a decompressed length — so a future artifact that changes the data fails with
/// a readable "expected X got Y" rather than silently stopping testing anything.
///
/// NOT RUN BY CI — see README.md in this directory.
/// </summary>
codeunit 64404 "Test Data LOB Values"
{
    Subtype = Test;

    var
        TdfAssert: Codeunit "TDF Assert";

    /// <summary>
    /// The one that would pass with the container stored verbatim. `Table Filter` is a
    /// compressed Blob: 46 bytes in the backup, 47 after BC's own Deflate is undone.
    /// </summary>
    [Test]
    procedure BlobContentIsTheDecompressedValue()
    var
        RetentionPolicySetupLine: Record "Retention Policy Setup Line";
        FilterStream: InStream;
        Content: Text;
    begin
        RetentionPolicySetupLine.Get(405, 10000);
        RetentionPolicySetupLine.CalcFields("Table Filter");
        TdfAssert.IsTrue(RetentionPolicySetupLine."Table Filter".HasValue(),
            'Table Filter should carry the backup bytes');

        RetentionPolicySetupLine."Table Filter".CreateInStream(FilterStream, TextEncoding::Windows);
        FilterStream.ReadText(Content, 45);

        // BC's container starts with 02 45 7D 5B, so reading 45 characters off an undecoded
        // blob cannot produce this string by accident.
        TdfAssert.AreEqual('VERSION(1) SORTING(Field1) WHERE(Field25=1(1))', Content,
            'the Blob must read back as its decompressed AL filter text, not as BC''s stored container');
    end;

    /// <summary>
    /// The other direction on the same type: a blob the backup stores as SQL NULL must read
    /// back as no value, and must not veto its table. #2268 — `Sales Header` refused over this
    /// one column.
    /// </summary>
    [Test]
    procedure ANullBlobHydratesAsNoValueAndNoLongerVetoesItsTable()
    var
        SalesHeader: Record "Sales Header";
    begin
        SalesHeader.Get(SalesHeader."Document Type"::Order, '101001');

        // The table is really populated, not merely present-and-empty.
        TdfAssert.AreEqual('10000', SalesHeader."Sell-to Customer No.",
            'Sales Header 101001 should carry its backup values');

        SalesHeader.CalcFields("Work Description");
        TdfAssert.IsFalse(SalesHeader."Work Description".HasValue(),
            'the backup stores a NULL here, so the Blob must read back with no value');
    end;

    /// <summary>Media: the stored media id, on two tables, one of them a table the runner's
    /// own demo-data story cares about.</summary>
    [Test]
    procedure MediaHydratesAsTheStoredMediaId()
    var
        WordTemplate: Record "Word Template";
        Customer: Record Customer;
    begin
        WordTemplate.Get('EVENT');
        TdfAssert.AreEqual('Customer Event', WordTemplate.Name, 'Word Template EVENT should be hydrated');
        TdfAssert.AreEqual('57C8E273-1769-4173-AAED-0A56E3ADCB8D',
            UpperCase(DelChr(Format(WordTemplate.Template.MediaId, 0, 4), '=', '{}')),
            'the Media field must carry the backup''s media id');

        Customer.Get('10000');
        TdfAssert.IsFalse(IsNullGuid(Customer.Image.MediaId),
            'Customer 10000 stores an Image media id in the backup, so it must not read back blank');
        TdfAssert.AreEqual('B66316B8-8275-4C96-8D5F-DF7B9FF7D9B0',
            UpperCase(DelChr(Format(Customer.Image.MediaId, 0, 4), '=', '{}')),
            'the Media field must carry the backup''s media id');
    end;

    /// <summary>MediaSet: the same claim on the other media-shaped type, which has its own
    /// NavValue and its own branch.</summary>
    [Test]
    procedure MediaSetHydratesAsTheStoredSetId()
    var
        ItemVariant: Record "Item Variant";
    begin
        ItemVariant.Get('SP-SCM1006', 'BLACK');
        TdfAssert.AreEqual('AutoDripLite - Black', ItemVariant.Description,
            'Item Variant SP-SCM1006/BLACK should be hydrated');
        TdfAssert.IsFalse(IsNullGuid(ItemVariant.Picture.MediaId),
            'this variant stores a picture set in the backup, so it must not read back blank');
        TdfAssert.AreEqual('EAAD9A16-3132-4C9C-8206-393598E9F1F0',
            UpperCase(DelChr(Format(ItemVariant.Picture.MediaId, 0, 4), '=', '{}')),
            'the MediaSet field must carry the backup''s media-set id');
    end;

    /// <summary>
    /// RecordId. CRONUS stores a blank one, so the value assertion is "blank" — which is why
    /// it is paired with a real field on the same record: a refused table would leave the
    /// RecordId blank too, and only the second assertion tells the two apart.
    /// </summary>
    [Test]
    procedure RecordIdHydratesAndNoLongerVetoesItsTable()
    var
        BankAccount: Record "Bank Account";
        BlankRecordId: RecordId;
    begin
        BankAccount.Get('CHECKING');
        TdfAssert.AreEqual('World Wide Bank', BankAccount.Name,
            'Bank Account CHECKING should carry its backup values');
        TdfAssert.AreEqual(Format(BlankRecordId), Format(BankAccount."Bank Stmt. Service Record ID"),
            'the backup stores a blank RecordId here, so it must read back blank');
    end;
}
