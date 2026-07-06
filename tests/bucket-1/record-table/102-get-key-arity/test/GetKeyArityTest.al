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
        // [THEN] No arity error fires and the call still finds the row —
        // guards the over-arity check against overshooting into rejecting
        // under-arity calls, which real BC accepts. Deliberately does not
        // pin HOW the missing trailing value binds; with a single row the
        // lookup semantics are indistinguishable.
        Clear(Rec);
        Assert.IsTrue(Rec.Get('D'), 'Under-arity Get must not raise the too-many-key-fields error');
        Assert.AreEqual('default-int', Rec.Payload, 'Under-arity Get must still find the row');
    end;
}
