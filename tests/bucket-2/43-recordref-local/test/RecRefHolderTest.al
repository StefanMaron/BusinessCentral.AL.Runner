codeunit 56441 "RecRef Holder Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure RecRefDeclOnlyDoesNotCascadeExclude()
    var
        Holder: Codeunit "RecRef Holder";
    begin
        // [GIVEN] A codeunit whose local var is `RecRef: RecordRef`
        // [WHEN] Calling any procedure on that codeunit
        // [THEN] The codeunit must stay included in compilation and return its sentinel
        Assert.AreEqual(1, Holder.DeclareOnly(), 'Declaring RecordRef should not exclude the codeunit');
    end;

    [Test]
    procedure RecRefClearLeavesCodeunitRunnable()
    var
        Holder: Codeunit "RecRef Holder";
    begin
        // Clear(RecRef) is a no-op; the procedure must still complete and return 2
        Assert.AreEqual(2, Holder.DeclareAndClear(), 'Clear(RecRef) must not abort execution');
    end;
}
