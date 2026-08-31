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
///   - `Company Information`.`Picture` is the Blob whose two lengths are furthest apart: 12,921
///     bytes stored, 15,225 after BC's own Deflate is undone. Asserting the decompressed
///     length AND the JPEG header proves the container was unwrapped rather than stored, in a
///     way `HasValue` cannot. (`Retention Policy Setup Line`.`Table Filter` holds readable
///     text and would read better, but the field is `internal` and AL will not let a test
///     touch it.)
///   - `Sales Header` is the table #2268 named. It refused over a NULL `Work Description` —
///     nothing about its own data — and it is the table a posting test needs. Both halves are
///     asserted: a real field's value (the table is really there) and the NULL blob reading
///     back as no value (the NULL is really a NULL).
///   - `Word Template` and `Customer` cover Media, `Item Variant` covers MediaSet. All three
///     assert the stored id, which is the whole of what BC's row read puts in the record —
///     the bytes behind an id live in Tenant Media, a table like any other.
///   - `Job Queue Entry` covers Duration, which only became reachable once the types above
///     stopped refusing the tables ahead of it.
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
codeunit 64407 "Test Data LOB Values"
{
    Subtype = Test;

    var
        TdfAssert: Codeunit "TDF Assert";

    /// <summary>
    /// The one that would pass with the container stored verbatim. `Picture` is a compressed
    /// Blob: 12,921 bytes in the backup, 15,225 after BC's own Deflate is undone. A codec that
    /// stored the container would give a blob that exists, has a value and has a plausible
    /// length — every assertion but these two.
    /// </summary>
    [Test]
    procedure BlobContentIsTheDecompressedValue()
    var
        CompanyInformation: Record "Company Information";
        PictureStream: InStream;
        FirstByte: Byte;
        SecondByte: Byte;
        ThirdByte: Byte;
        Header: Text;
    begin
        CompanyInformation.Get();
        CompanyInformation.CalcFields(Picture);
        TdfAssert.IsTrue(CompanyInformation.Picture.HasValue(),
            'Picture should carry the backup bytes');

        // 12921 is what the column stores; 15225 is the value. Stated as the expected number
        // so a failure reads "expected 15225, got 12921" and names the defect outright.
        TdfAssert.AreEqual(15225, CompanyInformation.Picture.Length(),
            'the Blob must be BC''s decompressed content, not the 12921-byte stored container');

        CompanyInformation.Picture.CreateInStream(PictureStream);
        PictureStream.Read(FirstByte, 1);
        PictureStream.Read(SecondByte, 1);
        PictureStream.Read(ThirdByte, 1);

        // Widened to Integer before asserting: AL formats a Byte as the CHARACTER it stands
        // for, so comparing Format(255) against Format(FirstByte) would read
        // "expected 255, got ÿ" on a passing case.
        Header := StrSubstNo('%1 %2 %3', FirstByte + 0, SecondByte + 0, ThirdByte + 0);

        // A JPEG starts FF D8 FF. BC's stored container starts 02 45 7D 5B, so this cannot
        // pass on undecoded bytes even if the length assertion somehow did.
        TdfAssert.AreEqual('255 216 255', Header,
            'the decoded Picture must start with a JPEG header');
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
    /// Duration. `Job Queue Entry`.`Job Timeout` is the one table in the shipped demo data
    /// that stores one, and it only became visible once the four types above stopped refusing
    /// the tables ahead of it.
    /// </summary>
    [Test]
    procedure DurationHydratesAsThatManyMilliseconds()
    var
        JobQueueEntry: Record "Job Queue Entry";
        TwelveHours: Duration;
        TwelveSeconds: Duration;
    begin
        JobQueueEntry.SetRange("Object ID to Run", 6700);
        TdfAssert.IsTrue(JobQueueEntry.FindFirst(), 'Job Queue Entry for object 6700 should be hydrated');

        // The backup stores 43,200,000 — twelve hours in milliseconds, BC's shipped default
        // job timeout. Compared as a Duration, not as the number, because AL formats a
        // Duration as "12 hours" and the assert helper compares formatted values.
        TwelveHours := 12 * 60 * 60 * 1000;
        TdfAssert.AreEqual(TwelveHours, JobQueueEntry."Job Timeout",
            'the Duration must read back as the milliseconds the backup stores');

        // The unit half of the same claim: a codec reading the bigint as seconds, or dividing
        // it, would land here instead.
        TwelveSeconds := 12 * 1000;
        TdfAssert.IsFalse(JobQueueEntry."Job Timeout" = TwelveSeconds,
            'the stored bigint is milliseconds, so 43200000 must not read back as twelve seconds');
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
