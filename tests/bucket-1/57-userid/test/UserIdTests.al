codeunit 56800 "UserId Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    // -----------------------------------------------------------------------
    // UserId() returns a non-empty string
    // -----------------------------------------------------------------------

    [Test]
    procedure UserIdIsNotEmpty()
    begin
        // [WHEN] UserId() is called
        // [THEN] The result is not empty — runner returns a stable stub value
        Assert.AreNotEqual('', UserId(), 'UserId() must return a non-empty value in the runner');
    end;

    // -----------------------------------------------------------------------
    // UserId() is consistent across calls
    // -----------------------------------------------------------------------

    [Test]
    procedure UserIdIsConsistent()
    var
        First: Text;
        Second: Text;
    begin
        // [WHEN] UserId() is called twice
        First := UserId();
        Second := UserId();

        // [THEN] Both calls return the same value
        Assert.AreEqual(First, Second, 'UserId() must return the same value across calls');
    end;
}
