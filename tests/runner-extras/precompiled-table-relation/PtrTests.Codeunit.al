// Issue #2528 — a PRECOMPILED dependency table's TableRelation must reach the runner's metadata.
//
// RUNNER-MECHANISM claim. What BC does with a TableRelation is plain BC behaviour, and it was
// already right here for a table the bundle under test compiles from AL SOURCE — the AL-source
// parser has carried RelationArms for a long time. What was missing is the runner's own
// reconstruction of that property for a table it never source-parsed: a Base Application table,
// read back from the dependency's SymbolReference.json. Before the fix `FieldRef.Relation`
// answered 0 for every one of the 7,787 relation-bearing Base Application fields, and
// `Validate()` skipped the relation check, so a value with no matching related row was silently
// ACCEPTED. That is a wrong answer, not a missing feature, which is why it is asserted rather
// than declared out of scope.
//
// Why here and not in the al-language corpus: the corpus is one app, so it has no way to express
// "a table this app did not compile". The BC half — that Validate refuses an unmatched relation
// value — needs no new corpus test, because it is the same platform behaviour the runner already
// satisfies for source-compiled tables.
//
// Customer."Currency Code" is the plain single-arm shape (TableRelation = Currency).
// Customer.City is deliberately included as the negative control: it carries
// ValidateTableRelation = 0 in the Base Application, so a fix that switched relation checking on
// wholesale — rather than reading BOTH properties — would make it raise, and this suite would go
// red.
codeunit 61401 "PTR Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    var
        Assert: Codeunit "PTR Assert";

    [Test]
    procedure FieldRefRelation_PrecompiledTable_AnswersTheRelatedTableId()
    var
        RecRef: RecordRef;
        FieldRef: FieldRef;
        Cust: Record Customer;
    begin
        RecRef.Open(Database::Customer);
        FieldRef := RecRef.Field(Cust.FieldNo("Currency Code"));

        // Concrete id, not "non-zero": table 4 is Currency. A reconstruction that returned some
        // arbitrary table would pass a non-zero check and fail this one.
        Assert.AreEqual(
            Database::Currency, FieldRef.Relation,
            'Customer."Currency Code" declares TableRelation = Currency in the Base Application, ' +
            'so FieldRef.Relation must answer table 4 — it answered 0 before issue #2528, because ' +
            'the symbol reader never read the TableRelation property');
    end;

    [Test]
    procedure FieldRefRelation_PrecompiledFieldWithNoRelation_AnswersZero()
    var
        RecRef: RecordRef;
        FieldRef: FieldRef;
        Cust: Record Customer;
    begin
        RecRef.Open(Database::Customer);
        FieldRef := RecRef.Field(Cust.FieldNo("Last Date Modified"));

        // The negative direction for the reader: a field with no TableRelation must stay 0, so
        // "everything answers non-zero now" cannot pass.
        Assert.AreEqual(
            0, FieldRef.Relation,
            'Customer."Last Date Modified" declares no TableRelation, so FieldRef.Relation must be 0');
    end;

    [Test]
    procedure Validate_PrecompiledRelationField_UnmatchedValue_RaisesBcsOwnError()
    var
        Cust: Record Customer;
    begin
        Cust.Init();
        Cust."No." := 'PTR-C-1';

        asserterror Cust.Validate("Currency Code", 'PTRNOSUCH');

        // BC's own message, not a runner-invented one. Asserting the text is what proves the
        // platform's relation check ran rather than something here refusing by hand.
        Assert.IsTrue(
            StrPos(GetLastErrorText(), 'cannot be found in the related table') > 0,
            'Validate against a non-existent related row must raise BC''s own relation error; got: '
            + GetLastErrorText());
        Assert.IsTrue(
            StrPos(GetLastErrorText(), 'Currency') > 0,
            'the error must name the related table (Currency); got: ' + GetLastErrorText());
    end;

    [Test]
    procedure Validate_PrecompiledRelationField_MatchedValue_IsAccepted()
    var
        Cust: Record Customer;
        Currency: Record Currency;
    begin
        // Positive direction: the relation must not refuse a value that DOES exist. Without this,
        // a "reconstruction" that refused everything would pass the negative test above.
        if not Currency.Get('PTRCUR') then begin
            Currency.Init();
            Currency.Code := 'PTRCUR';
            Currency.Insert();
        end;

        Cust.Init();
        Cust."No." := 'PTR-C-2';
        Cust.Validate("Currency Code", 'PTRCUR');

        Assert.AreEqual('PTRCUR', Cust."Currency Code",
            'a related row that exists must be accepted and stored');
    end;

    [Test]
    procedure Validate_PrecompiledFieldWithValidateTableRelationOff_DoesNotRaise()
    var
        Cust: Record Customer;
    begin
        // The negative control for the SECOND property. Customer.City carries a TableRelation to
        // "Post Code".City AND ValidateTableRelation = 0, so BC accepts an unmatched value here.
        // A fix that turned relation checking on wholesale would raise and fail this test.
        Cust.Init();
        Cust."No." := 'PTR-C-3';
        Cust.Validate(City, 'PTRNOSUCHCITY');

        Assert.AreEqual('PTRNOSUCHCITY', Cust.City,
            'Customer.City declares ValidateTableRelation = 0, so an unmatched value must be ' +
            'accepted — the fix must read that property, not just TableRelation');
    end;
}
