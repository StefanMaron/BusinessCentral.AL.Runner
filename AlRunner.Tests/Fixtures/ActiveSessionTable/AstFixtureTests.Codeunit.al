// Fixture suite for ActiveSessionTableTests.cs (#3233). RUNNER-MECHANISM: the row the runner
// seeds is read back from session state. What a real tier answers is adjudicated upstream by
// "Test Active Session Table" (corpus codeunit 60976).
//
// ServiceInstanceId() and SessionId() are seeded positive (#5144), so the key is no longer an
// Integer's default; User ID, User SID, Login Datetime and Session Unique ID still discriminate
// a row of defaults.
codeunit 70581 "AST Fixture Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit "AST Assert";

    [Test]
    procedure ActiveSession_GetByInstanceAndSessionId_FindsARow()
    var
        ActiveSession: Record "Active Session";
    begin
        Assert.IsTrue(ActiveSession.Get(ServiceInstanceId(), SessionId()),
            'Active Session must hold a row for (ServiceInstanceId(), SessionId())');
        Assert.AreEqual(1, ActiveSession.Count(), 'exactly one Active Session row');
    end;

    [Test]
    procedure ActiveSession_Row_UserIdAndSidAreTheSessionUser()
    var
        ActiveSession: Record "Active Session";
    begin
        ActiveSession.Get(ServiceInstanceId(), SessionId());
        Assert.AreNotEqual('', ActiveSession."User ID", 'User ID must not be blank');
        Assert.AreEqual(UserId(), ActiveSession."User ID", 'User ID must be UserId()');
        Assert.IsFalse(IsNullGuid(ActiveSession."User SID"), 'User SID must not be null');
        Assert.AreEqual(UserSecurityId(), ActiveSession."User SID", 'User SID must be UserSecurityId()');
    end;

    [Test]
    procedure ActiveSession_Row_LoginDatetimeIsTheSessionTablesLoginInstant()
    // The two tables read one login instant: Active Session stores it as a DateTime, Session
    // splits it into date and time in the session's time zone. A UTC/local slip shows here.
    var
        ActiveSession: Record "Active Session";
        Sess: Record Session;
    begin
        ActiveSession.Get(ServiceInstanceId(), SessionId());
        Sess.Get(SessionId());
        Assert.AreNotEqual(0DT, ActiveSession."Login Datetime", 'Login Datetime must be answered');
        Assert.AreEqual(CreateDateTime(Sess."Login Date", Sess."Login Time"), ActiveSession."Login Datetime",
            'Active Session."Login Datetime" must be the instant Session reports as Login Date/Time');
    end;

    [Test]
    procedure ActiveSession_Row_CarriesASessionUniqueId()
    var
        ActiveSession: Record "Active Session";
    begin
        ActiveSession.Get(ServiceInstanceId(), SessionId());
        Assert.IsFalse(IsNullGuid(ActiveSession."Session Unique ID"), 'Session Unique ID must not be null');
    end;

    [Test]
    procedure ActiveSession_Row_ClientTypeIsBcsMappingOfTheSkeletonConnectionType()
    // A CONSTANT, not an observation: the skeleton session's ClientConnectionType is never set,
    // and BC's own SessionEventTableHandler.TranslateToClientType has no entry for that value,
    // so it answers Unknown. Pinned so a change to either side is noticed. Not compared against
    // CurrentClientType(): that maps the same value to Windows through a different switch, and
    // the option captions differ by construction.
    var
        ActiveSession: Record "Active Session";
    begin
        ActiveSession.Get(ServiceInstanceId(), SessionId());
        Assert.AreEqual('Unknown', Format(ActiveSession."Client Type"),
            'Client Type must be BC''s TranslateToClientType of the skeleton connection type');
    end;

    [Test]
    procedure ActiveSession_GetOnASessionIdThatIsNoSession_ReturnsFalse()
    var
        ActiveSession: Record "Active Session";
    begin
        Assert.IsFalse(ActiveSession.Get(ServiceInstanceId(), -987654),
            'a session id belonging to no session must not resolve to a row');
    end;

    [Test]
    procedure ActiveSession_KeyIsThePositiveSessionIdentity()
    // #5144: a service tier's ids are positive (corpus 60023), and the row carries the same pair.
    var
        ActiveSession: Record "Active Session";
    begin
        Assert.IsTrue(ServiceInstanceId() > 0, StrSubstNo('ServiceInstanceId() must be positive, got %1', ServiceInstanceId()));
        Assert.IsTrue(SessionId() > 0, StrSubstNo('SessionId() must be positive, got %1', SessionId()));
        Assert.IsTrue(ActiveSession.Get(ServiceInstanceId(), SessionId()), 'Active Session must hold the reading session');
        Assert.AreEqual(ServiceInstanceId(), ActiveSession."Server Instance ID", 'row Server Instance ID');
        Assert.AreEqual(SessionId(), ActiveSession."Session ID", 'row Session ID');
        Assert.IsFalse(ActiveSession.Get(0, SessionId()), 'no row may sit under server instance id 0');
    end;
}
