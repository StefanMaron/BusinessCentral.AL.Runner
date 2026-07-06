codeunit 50135 "Get Key Arity Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure GetWithExactKeyArityRetrievesRow()
    var
        Rec: Record "Two Key Arity Table";
    begin
        // [GIVEN] A row keyed by the 2-field primary key
        Rec.Init();
        Rec."Code 1" := 'A';
        Rec."Int 1" := 1;
        Rec.Payload := 'hello';
        Rec.Insert(false);

        // [WHEN] Getting with exactly as many values as PK fields
        Clear(Rec);
        Assert.IsTrue(Rec.Get('A', 1), 'Get with exact key arity must retrieve the row');

        // [THEN] The retrieved row carries the non-key payload
        Assert.AreEqual('hello', Rec.Payload, 'Retrieved row must carry the payload');
    end;

    [Test]
    procedure GetWithTooManyKeyValuesErrors()
    var
        Rec: Record "Two Key Arity Table";
    begin
        // [GIVEN] A row keyed by the 2-field primary key
        Rec.Init();
        Rec."Code 1" := 'A';
        Rec."Int 1" := 1;
        Rec.Insert(false);

        // [WHEN] Getting with 3 values against a 2-field PK
        // [THEN] The platform error fires, quoting the table NAME (not the
        // Caption — verified on a real BC 28.1 container)
        asserterror Rec.Get('A', 1, 42);
        Assert.ExpectedError('Too many key fields were specified, so "Two Key Arity Table" could not be retrieved. The number of fields in the primary key is 2.');
    end;

    [Test]
    procedure ConsumedOverKeyedGetAlsoErrors()
    var
        Rec: Record "Two Key Arity Table";
    begin
        // [GIVEN] A row keyed by the 2-field primary key
        Rec.Init();
        Rec."Code 1" := 'A';
        Rec."Int 1" := 1;
        Rec.Insert(false);

        // [WHEN] The over-keyed Get's return value is consumed by an if
        // [THEN] The error still fires — real BC raises it unconditionally,
        // it is not a "record not found" condition (container-verified)
        asserterror
            if Rec.Get('A', 1, 42) then;
        Assert.ExpectedError('Too many key fields were specified');
    end;

    [Test]
    procedure GetWithFewerKeyValuesStillSucceeds()
    var
        Rec: Record "Two Key Arity Table";
    begin
        // [GIVEN] A row whose trailing PK field is the type default
        Rec.Init();
        Rec."Code 1" := 'D';
        Rec."Int 1" := 0;
        Rec.Payload := 'default-int';
        Rec.Insert(false);

        // [WHEN] Getting with fewer values than PK fields
        // [THEN] No arity error fires and the call finds the row: the
        // missing trailing key value binds as the Integer default 0,
        // which is exactly this row's key
        Clear(Rec);
        Assert.IsTrue(Rec.Get('D'), 'Under-arity Get must not raise the too-many-key-fields error');
        Assert.AreEqual('default-int', Rec.Payload, 'Under-arity Get must still find the row');
    end;

    [Test]
    procedure UnderArityGetBindsDefaultsNotPrefix()
    var
        Rec: Record "Two Key Arity Table";
    begin
        // [GIVEN] A row whose trailing PK value is NOT the type default
        Rec.Init();
        Rec."Code 1" := 'E';
        Rec."Int 1" := 5;
        Rec.Insert(false);

        // [WHEN] Getting with only the leading key value
        // [THEN] Real BC binds the missing "Int 1" as 0 and looks up
        // ('E', 0), which does not exist — a prefix match on ('E', *)
        // would wrongly return the ('E', 5) row (container-verified
        // against BC 28.1)
        Assert.IsFalse(Rec.Get('E'), 'Under-arity Get must bind the missing trailing key value as the type default, not prefix-match');

        // [THEN] The unconsumed form raises the record-not-found error
        asserterror Rec.Get('E');
        Assert.ExpectedError('does not exist');
    end;

    [Test]
    procedure UnderArityGetPicksDefaultKeyedRowAmongSiblings()
    var
        Rec: Record "Two Key Arity Table";
    begin
        // [GIVEN] Two rows sharing the leading key value; the one with the
        // non-default trailing key inserted FIRST, so a prefix match would
        // surface it instead of the default-keyed row
        Rec.Init();
        Rec."Code 1" := 'F';
        Rec."Int 1" := 5;
        Rec.Payload := 'five';
        Rec.Insert(false);

        Rec.Init();
        Rec."Code 1" := 'F';
        Rec."Int 1" := 0;
        Rec.Payload := 'zero';
        Rec.Insert(false);

        // [WHEN] Getting with only the leading key value
        // [THEN] The missing trailing value binds as 0 and the exact
        // ('F', 0) row is returned, not the first prefix match
        Clear(Rec);
        Assert.IsTrue(Rec.Get('F'), 'Under-arity Get must find the row keyed by the type default');
        Assert.AreEqual('zero', Rec.Payload, 'Under-arity Get must bind the default-keyed row, not the first row sharing the leading key value');
    end;

    [Test]
    procedure UnderArityGetOnDurationPkBindsDefaultsNotPrefix()
    var
        Rec: Record "Duration Key Arity Table";
    begin
        // [GIVEN] A row whose trailing Duration PK value is NOT the type default
        Rec.Init();
        Rec."Code 1" := 'H';
        Rec."Dur 1" := 5000;
        Rec.Insert(false);

        // [WHEN] Getting with only the leading key value
        // [THEN] The missing "Dur 1" binds as the Duration default 0 and the
        // lookup targets ('H', 0), which does not exist — a prefix match
        // would wrongly return the ('H', 5000ms) row
        Assert.IsFalse(Rec.Get('H'), 'Under-arity Get on a Duration PK must bind the missing key value as the Duration default, not prefix-match');

        // [THEN] The unconsumed form raises the record-not-found error
        asserterror Rec.Get('H');
        Assert.ExpectedError('does not exist');
    end;

    [Test]
    procedure UnderArityGetOnDurationPkPicksDefaultKeyedRowAmongSiblings()
    var
        Rec: Record "Duration Key Arity Table";
    begin
        // [GIVEN] Two rows sharing the leading key value; the non-default
        // Duration inserted FIRST, so a prefix match would surface it
        Rec.Init();
        Rec."Code 1" := 'G';
        Rec."Dur 1" := 5000;
        Rec.Payload := 'five-seconds';
        Rec.Insert(false);

        Rec.Init();
        Rec."Code 1" := 'G';
        Rec."Dur 1" := 0;
        Rec.Payload := 'zero';
        Rec.Insert(false);

        // [WHEN] Getting with only the leading key value
        // [THEN] The exact ('G', 0) row is returned, not the first prefix match
        Clear(Rec);
        Assert.IsTrue(Rec.Get('G'), 'Under-arity Get must find the Duration-default-keyed row');
        Assert.AreEqual('zero', Rec.Payload, 'Under-arity Get must bind the Duration-default row, not the first row sharing the leading key value');
    end;

    [Test]
    procedure FullArityGetFindsRowWithUnassignedTrailingPk()
    var
        Rec: Record "Two Key Arity Table";
    begin
        // [GIVEN] A row inserted without ever assigning the trailing PK
        // field — real BC stores it as the type default 0
        Rec.Init();
        Rec."Code 1" := 'U';
        Rec.Payload := 'unassigned-int';
        Rec.Insert(false);

        // [WHEN] Getting with the full PK, spelling the default explicitly
        // [THEN] The row is found — an unassigned PK field compares as its
        // type default, not as an empty value distinct from 0
        Clear(Rec);
        Assert.IsTrue(Rec.Get('U', 0), 'Full-arity Get with the explicit default must find the row whose trailing PK field was never assigned');
        Assert.AreEqual('unassigned-int', Rec.Payload, 'Retrieved row must carry the payload');

        // [THEN] A non-default value still misses
        Assert.IsFalse(Rec.Get('U', 5), 'Full-arity Get with a non-default value must not find the default-keyed row');
    end;

    [Test]
    procedure ZeroArgGetBindsAllPkDefaults()
    var
        Rec: Record "Two Key Arity Table";
    begin
        // [GIVEN] Only a row keyed by a non-default value
        Rec.Init();
        Rec."Code 1" := 'S';
        Rec."Int 1" := 5;
        Rec.Insert(false);

        // [WHEN] Getting with zero key values
        // [THEN] Every PK field binds to its type default — ('', 0) — so the
        // 'S' row must not match (first-row semantics would return it)
        Assert.IsFalse(Rec.Get(), 'Zero-arg Get must bind all PK fields to defaults, not return the first row');
        asserterror Rec.Get();
        Assert.ExpectedError('does not exist');

        // [GIVEN] A row keyed entirely by defaults
        Rec.Init();
        Rec."Code 1" := '';
        Rec."Int 1" := 0;
        Rec.Payload := 'blank';
        Rec.Insert(false);

        // [THEN] Zero-arg Get finds exactly that row
        Clear(Rec);
        Assert.IsTrue(Rec.Get(), 'Zero-arg Get must find the all-defaults row');
        Assert.AreEqual('blank', Rec.Payload, 'Zero-arg Get must bind the all-defaults row');
    end;

    [Test]
    procedure UnderArityNotFoundErrorListsAllPkFieldsWithBoundDefaults()
    var
        Rec: Record "Two Key Arity Table";
    begin
        // [WHEN] An under-arity Get misses in the unconsumed form
        // [THEN] The error lists every PK field by name with its value,
        // including the bound trailing default — container-verified on
        // BC 28.1: "Identification fields and values: Code 1='Q',Int 1='0'"
        asserterror Rec.Get('Q');
        Assert.ExpectedError('Identification fields and values: Code 1=''Q'',Int 1=''0''');
    end;

    [Test]
    procedure GetOnParenNamedPkWithExactAritySucceeds()
    var
        Rec: Record "Paren PK Table";
    begin
        // [GIVEN] A row in a table whose first PK field name contains
        // parentheses (cf. BaseApp names like "Amount (LCY)")
        Rec.Init();
        Rec."Code (Main)" := 'X';
        Rec."Line No." := 7;
        Rec.Payload := 'paren-row';
        Rec.Insert(false);

        // [WHEN] Getting with exactly as many values as the 2-field PK
        // [THEN] The call succeeds — the paren in the quoted field name must
        // not shrink the PK the arity check compares against
        Clear(Rec);
        Assert.IsTrue(Rec.Get('X', 7), 'Exact-arity Get on a paren-named PK field must retrieve the row');
        Assert.AreEqual('paren-row', Rec.Payload, 'Retrieved row must carry the payload');
    end;

    [Test]
    procedure GetOnParenNamedPkOverArityErrors()
    var
        Rec: Record "Paren PK Table";
    begin
        // [GIVEN] A row in the paren-named-PK table
        Rec.Init();
        Rec."Code (Main)" := 'X';
        Rec."Line No." := 7;
        Rec.Insert(false);

        // [WHEN] Getting with 3 values against the 2-field PK
        // [THEN] The platform error fires and reports the true PK width, 2
        asserterror Rec.Get('X', 7, 42);
        Assert.ExpectedError('Too many key fields were specified, so "Paren PK Table" could not be retrieved. The number of fields in the primary key is 2.');
    end;
}
