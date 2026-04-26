/// Tests that the runner pre-seeds the User system table (2000000120)
/// with a record matching the configured user, so AL code that calls
/// User.Get(UserSecurityId()) succeeds without "record not found" errors.
codeunit 227001 "User Table Seed Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    /// Positive: User.Get(UserSecurityId()) must succeed — the runner must
    /// have pre-seeded the User table so the current user's record exists.
    [Test]
    procedure UserGet_BySecurityId_Succeeds()
    var
        User: Record User;
    begin
        // [GIVEN] The runner pre-seeds the User table before each test
        // [WHEN]  User.Get is called with the current UserSecurityId
        // [THEN]  The record is found — no "record not found" error
        Assert.IsTrue(User.Get(UserSecurityId()), 'User.Get(UserSecurityId()) must succeed in al-runner');
    end;

    /// Positive: the seeded User record must carry the correct "User Name"
    /// matching UserId() — proves the seeded row is not an empty stub.
    [Test]
    procedure UserGet_BySecurityId_HasCorrectUserName()
    var
        User: Record User;
    begin
        // [GIVEN] Runner has pre-seeded the User table
        // [WHEN]  The record is retrieved by UserSecurityId
        User.Get(UserSecurityId());
        // [THEN]  "User Name" matches the configured UserId() value
        Assert.AreEqual(UserId(), User."User Name", 'Seeded User record must have User Name = UserId()');
    end;

    /// Positive: User table contains exactly one row — not empty, not duplicated.
    [Test]
    procedure UserTable_ContainsExactlyOneRow()
    var
        User: Record User;
    begin
        Assert.AreEqual(1, User.Count(), 'User table must contain exactly one pre-seeded row');
    end;

    /// Negative: looking up a non-existent security ID must return false (not crash).
    [Test]
    procedure UserGet_UnknownGuid_ReturnsFalse()
    var
        User: Record User;
        UnknownGuid: Guid;
    begin
        // [GIVEN] A GUID that was never inserted
        Evaluate(UnknownGuid, '{FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF}');
        // [WHEN/THEN] Get returns false — no exception, no "record not found" crash
        Assert.IsFalse(User.Get(UnknownGuid), 'Get on unknown GUID must return false, not throw');
    end;
}
