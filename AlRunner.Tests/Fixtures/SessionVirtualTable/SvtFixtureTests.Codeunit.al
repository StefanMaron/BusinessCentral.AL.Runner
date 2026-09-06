// Fixture suite for SessionVirtualTableTests.cs (#2940).
//
// A RUNNER-MECHANISM suite. What real BC answers for the Session virtual table (2000000009)
// is adjudicated upstream, by "Test Session Virtual Table" in
// StefanMaron/BusinessCentral.AL.Language.Tests, against a live service tier.
//
// What is pinned HERE is the property that made this fixable at all: the runner already knows
// who it is — SessionId() and UserId() answer from the skeleton NavSession — and the row in
// the table has to be READ BACK from that same state rather than recomputed. So every
// identity assertion below compares the table against the session surface instead of against
// a literal. A populator that invented a connection id, or wrote a blank user, passes none of
// them; a populator that inserted one row of per-field defaults fails at the very first
// SetRange("My Session", true) because that column would be false.
//
// One qualification, measured rather than assumed, because the arms below would otherwise be
// read as claiming more than they do: the runner's own SessionId() is 0. NavSession's `Id = -1`
// field initializer never runs, because the skeleton session is built with
// RuntimeHelpers.GetUninitializedObject. 0 is also an Integer column's default, so a populator
// that set "My Session" = true and left every other column at its default WOULD pass the
// "Connection ID" arm and the Get(SessionId()) arm. Those two are coupling assertions — the
// column must be whatever SessionId() says, today and after SessionId() changes — not
// discrimination assertions. The three that do discriminate such a row are User ID, Login
// Date/Time and Host Name.
codeunit 70561 "SVT Fixture Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit "SVT Assert";

    [Test]
    procedure Session_HasARowForTheReadingSession()
    var
        Sess: Record Session;
    begin
        // The direct RED: before the fix this table answered zero rows, so FindSet() was
        // false and nothing said the data was absent.
        Assert.IsTrue(Sess.FindSet(), 'Session must answer at least one row.');

        Sess.SetRange("My Session", true);
        Assert.AreEqual(1, Sess.Count(), 'exactly one row must be flagged as the reading session.');
    end;

    [Test]
    procedure Session_MySessionRow_ConnectionIdIsWhatSessionIdReturns()
    var
        Sess: Record Session;
    begin
        // The read-it-back claim: a populator that fabricated a connection id fails here even
        // though the row would exist and the count would be right. It does NOT rule out a row
        // left at its column defaults — SessionId() is 0 in the runner and so is an Integer
        // default; see the note at the top of this file, and the three arms that do.
        Sess.SetRange("My Session", true);
        Assert.IsTrue(Sess.FindFirst(), 'the reading session must be a row.');
        Assert.AreEqual(SessionId(), Sess."Connection ID",
            'Session."Connection ID" must be the id SessionId() reports for this session.');
    end;

    [Test]
    procedure Session_MySessionRow_UserIdIsWhatUserIdReturns()
    var
        Sess: Record Session;
    begin
        Sess.SetRange("My Session", true);
        Assert.IsTrue(Sess.FindFirst(), 'the reading session must be a row.');
        Assert.AreNotEqual('', Sess."User ID", 'Session."User ID" must not be blank.');
        Assert.AreEqual(UserId(), Sess."User ID",
            'Session."User ID" must be the user UserId() reports for this session.');
    end;

    [Test]
    procedure Session_Get_ByConnectionId_FindsTheSameRow()
    var
        ByGet: Record Session;
        ByFind: Record Session;
    begin
        // Get() reaches the row by primary key, so a provider whose key columns disagree with
        // the row's own "Connection ID" fails here while FindFirst still succeeds. Same
        // qualification as the arm above: with SessionId() = 0 this is a coupling assertion,
        // not a discrimination one.
        Assert.IsTrue(ByGet.Get(SessionId()), 'Get(SessionId()) must find the reading session.');
        Assert.IsTrue(ByGet."My Session", 'the row Get returns must be flagged as this session.');

        ByFind.SetRange("My Session", true);
        ByFind.FindFirst();
        Assert.AreEqual(ByFind."Connection ID", ByGet."Connection ID",
            'Get and FindFirst must reach the same row.');
        Assert.AreEqual(ByFind."User ID", ByGet."User ID",
            'Get and FindFirst must reach the same row.');
    end;

    [Test]
    procedure Session_MySessionRow_CarriesALoginDateAndTime()
    var
        Sess: Record Session;
    begin
        // Rules out a row inserted with BC's per-field defaults everywhere but the key.
        Sess.SetRange("My Session", true);
        Sess.FindFirst();
        Assert.AreNotEqual(0D, Sess."Login Date", 'Session."Login Date" must be answered.');
        Assert.AreNotEqual(0T, Sess."Login Time", 'Session."Login Time" must be answered.');
    end;

    [Test]
    procedure Session_MySessionRow_CarriesAHostName()
    var
        Sess: Record Session;
    begin
        // BC's SessionDataProvider answers this from the machine hosting the session; so does
        // the runner. Shape only — the name is a property of the host, never asserted.
        Sess.SetRange("My Session", true);
        Sess.FindFirst();
        Assert.AreNotEqual('', Sess."Host Name", 'Session."Host Name" must be answered.');
    end;

    [Test]
    procedure Session_GetOnAConnectionIdThatIsNotThisSession_ReturnsFalse()
    var
        Sess: Record Session;
    begin
        // Negative. Also the assertion that fails if the populator padded the table with
        // rows it cannot account for.
        Assert.IsFalse(Sess.Get(SessionId() + 987654),
            'a connection id belonging to no session must not resolve to a row.');
    end;

    [Test]
    procedure Session_FilterOnMySessionFalse_SelectsNothing()
    var
        Sess: Record Session;
    begin
        // Negative, and it discriminates: it fails against a table padded with rows that are
        // not the reading session, and against a "My Session" column left at its default.
        Sess.SetRange("My Session", false);
        Assert.IsTrue(Sess.IsEmpty(), 'no row may claim to be a session other than this one.');
    end;
}
