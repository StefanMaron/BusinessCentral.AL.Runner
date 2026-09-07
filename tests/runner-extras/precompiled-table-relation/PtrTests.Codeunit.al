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
//
// "My Item"."User ID" is the negative control for the SECOND property: TableRelation =
// User."User Name" (single arm, which this parser accepts), ValidateTableRelation = false, and —
// the part that took two attempts to get right — NO OnValidate trigger, so nothing but the
// relation check can raise. A fix that switched relation checking on wholesale rather than
// reading BOTH properties makes this raise and the suite goes red. Verified by deleting the
// relationValidate line in BcAppSymbolCache: this test, and only this test, fails.
//
// Item."Base Unit of Measure" and User Setup."User ID" were both tried first and are unusable:
// each carries its own OnValidate that rejects an unmatched value in Microsoft's AL ("The Unit of
// Measure with Code X does not exist"), so they raise whatever the flag says and prove nothing
// about it.
//
// The control was originally Customer.City, which was VACUOUS at the time: City's relation is
// the two-arm conditional `if (...) "Post Code".City else if (...) ... where(...)`, and the
// where-clause's `field(...)` link hit RelationConditionList's default arm, so ParseRelationArms
// refused the WHOLE property and the field reached the metadata with no relations at all. It
// would have passed identically with the ValidateTableRelation read deleted.
//
// #2518 has since taught RelationConditionList to carry a `field(...)` link in a where() clause,
// so Customer.City's relation IS read now and the field is no longer a place a vacuous control
// can hide. The control stays on "My Item"."User ID" anyway: City's own OnValidate is not the
// point, and the single-arm shape keeps the control's claim about ValidateTableRelation clean.
// The flag is not cosmetic: 235 Base Application fields carry
// ValidateTableRelation = 0 together with a relation this parser accepts (about 150 of them user-id
// fields), and if that read regressed they would start REFUSING values real BC accepts — the same
// silent wrongness this PR fixes, inverted.
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
    procedure FieldRefRelation_FieldWithValidateTableRelationOff_StillAnswersTheRelation()
    var
        RecRef: RecordRef;
        FieldRef: FieldRef;
        MyItm: Record "My Item";
    begin
        // Precondition for the control below, asserted rather than assumed: the relation must
        // actually be PRESENT on this field. If it were refused by the parser — as Customer.City's
        // conditional form was before #2518 — the "does not raise" test underneath would pass for
        // the wrong reason and prove nothing about ValidateTableRelation. This assertion is what
        // makes the control non-vacuous, and it is the check the first version of this suite was
        // missing. It stays whether or not any particular shape is currently refused, because it
        // is the refusal DISCIPLINE, not one shape, that can hollow the control out.
        RecRef.Open(Database::"My Item");
        FieldRef := RecRef.Field(MyItm.FieldNo("User ID"));

        Assert.AreEqual(
            Database::User, FieldRef.Relation,
            '"My Item"."User ID" declares TableRelation = User."User Name", so the relation must ' +
            'be present even though ValidateTableRelation is false — otherwise the ' +
            'ValidateTableRelation control below is vacuous');
    end;

    [Test]
    procedure Validate_PrecompiledFieldWithValidateTableRelationOff_DoesNotRaise()
    var
        MyItm: Record "My Item";
    begin
        // The negative control for the SECOND property, on a field whose relation the parser DOES
        // accept (the test above pins that) and which carries no OnValidate of its own, so the
        // relation check is the only thing that could raise. "My Item"."User ID" declares
        // TableRelation = User."User Name" AND ValidateTableRelation = false, so BC accepts an
        // unmatched value. Deleting the relationValidate read in BcAppSymbolCache makes this test
        // raise and fail — that is what makes it a control rather than a comment.
        MyItm.Init();
        MyItm.Validate("User ID", 'PTRNOUSER');

        Assert.AreEqual('PTRNOUSER', MyItm."User ID",
            '"My Item"."User ID" declares ValidateTableRelation = false, so an unmatched value ' +
            'must be accepted — the fix must read that property, not just TableRelation');
    end;

    // ------------------------------------------------------------------------------------------
    // #3177 — the same property, on a field a precompiled TABLEEXTENSION contributes.
    //
    // Everything above reads a field declared by the Base Application TABLE itself, which #2528
    // fixed. TryParseTableExtensionSymbol is an intentional copy of that loop and never got the
    // change, so a field a tableextension adds reached the metadata with no relation at all. The
    // C# tests in AlRunner.Tests/BcAppSymbolCacheTableExtRelationTests.cs assert the parsed
    // SYMBOL; these assert that the parsed symbol actually reaches FieldRef.Relation and the
    // platform's relation check, which is the part nothing else covers.
    //
    // Measured from the BC 28.1 package rather than assumed:
    //   * tableextension 6450 "Serv. Customer" targets Customer and declares
    //     field(5900; "Service Zone Code"; Code[10]) { TableRelation = "Service Zone"; }
    //   * table 5957 is "Service Zone".
    //   * that field carries NO OnValidate trigger and NO ValidateTableRelation — read out of
    //     Microsoft's own src/Service/Sales/Customer/ServCustomer.TableExt.al — so the relation
    //     check is the only thing in the platform that can raise on it. A Validate test here
    //     cannot pass for some other reason.
    //   * field 5930 "Combine Service Shipments" is the other Normal-class field the same
    //     tableextension adds, and it declares no TableRelation.
    // ------------------------------------------------------------------------------------------

    [Test]
    procedure FieldRefRelation_PrecompiledTableExtensionField_AnswersTheRelatedTableId()
    var
        RecRef: RecordRef;
        FieldRef: FieldRef;
        Cust: Record Customer;
    begin
        RecRef.Open(Database::Customer);
        FieldRef := RecRef.Field(Cust.FieldNo("Service Zone Code"));

        // Concrete id: table 5957 is "Service Zone". Before #3177 this answered 0 — the
        // tableextension field-parse loop never read the TableRelation property at all — so a
        // non-zero check would already discriminate, but the id is asserted because a
        // reconstruction that attached SOME arbitrary relation would pass that and fail this.
        Assert.AreEqual(
            Database::"Service Zone", FieldRef.Relation,
            'Customer."Service Zone Code" (field 5900) is contributed by tableextension 6450 ' +
            '"Serv. Customer" with TableRelation = "Service Zone", so FieldRef.Relation must ' +
            'answer table 5957 — it answered 0 before issue #3177');
    end;

    [Test]
    procedure FieldRefRelation_PrecompiledTableExtensionFieldWithNoRelation_AnswersZero()
    var
        RecRef: RecordRef;
        FieldRef: FieldRef;
        Cust: Record Customer;
    begin
        // The negative direction, on the SAME tableextension: 5930 "Combine Service Shipments"
        // is Normal-class and declares no TableRelation, so it must stay 0. Without it, a reader
        // that attached a relation to every extension field would pass the test above.
        RecRef.Open(Database::Customer);
        FieldRef := RecRef.Field(Cust.FieldNo("Combine Service Shipments"));

        Assert.AreEqual(
            0, FieldRef.Relation,
            'Customer."Combine Service Shipments" (field 5930, same tableextension 6450) ' +
            'declares no TableRelation, so FieldRef.Relation must be 0');
    end;

    [Test]
    procedure Validate_PrecompiledTableExtensionRelationField_UnmatchedValue_RaisesBcsOwnError()
    var
        Cust: Record Customer;
    begin
        Cust.Init();
        Cust."No." := 'PTR-C-3';

        asserterror Cust.Validate("Service Zone Code", 'PTRNOZONE');

        // BC's own message. The field has no OnValidate of its own, so the relation check is the
        // only thing that can raise here — which is what makes this prove the relation reached
        // the platform rather than merely reaching FieldRef.Relation.
        Assert.IsTrue(
            StrPos(GetLastErrorText(), 'cannot be found in the related table') > 0,
            'Validate against a non-existent Service Zone must raise BC''s own relation error; got: '
            + GetLastErrorText());
        Assert.IsTrue(
            StrPos(GetLastErrorText(), 'Service Zone') > 0,
            'the error must name the related table (Service Zone); got: ' + GetLastErrorText());
    end;

    [Test]
    procedure Validate_PrecompiledTableExtensionRelationField_MatchedValue_IsAccepted()
    var
        Cust: Record Customer;
        ServiceZone: Record "Service Zone";
    begin
        // Positive direction: a relation that refused everything would pass the test above and
        // fail this one.
        if not ServiceZone.Get('PTRZONE') then begin
            ServiceZone.Init();
            ServiceZone.Code := 'PTRZONE';
            ServiceZone.Insert();
        end;

        Cust.Init();
        Cust."No." := 'PTR-C-4';
        Cust.Validate("Service Zone Code", 'PTRZONE');

        Assert.AreEqual('PTRZONE', Cust."Service Zone Code",
            'a Service Zone row that exists must be accepted and stored');
    end;
}
