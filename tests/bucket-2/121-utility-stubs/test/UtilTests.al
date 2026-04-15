codeunit 96002 "Util Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Helper: Codeunit "Util Helper";

    [Test]
    procedure ClosingDateReturnsDate()
    var
        d: Date;
    begin
        d := Helper.GetClosingDate(20240115D);
        Assert.AreNotEqual(0D, d, 'ClosingDate should return a non-zero date');
    end;

    [Test]
    procedure NormalDateReturnsDate()
    var
        d: Date;
    begin
        d := Helper.GetNormalDate(20240115D);
        Assert.AreEqual(20240115D, d, 'NormalDate should return the same date for a normal date');
    end;

    [Test]
    procedure ClosingDateThenNormalDateRoundTrips()
    var
        original: Date;
        closing: Date;
        normal: Date;
    begin
        original := 20240630D;
        closing := Helper.GetClosingDate(original);
        normal := Helper.GetNormalDate(closing);
        Assert.AreEqual(original, normal, 'NormalDate(ClosingDate(d)) should return original date');
    end;

    [Test]
    procedure ProductNameFullContainsBC()
    var
        name: Text;
    begin
        name := Helper.GetProductNameFull();
        Assert.IsTrue(name.Contains('Business Central'), 'ProductName.Full() should contain Business Central');
    end;

    [Test]
    procedure ProductNameShortContainsBC()
    var
        name: Text;
    begin
        name := Helper.GetProductNameShort();
        Assert.IsTrue(name.Contains('Business Central'), 'ProductName.Short() should contain Business Central');
    end;

    [Test]
    procedure ProductNameMarketingContainsBC()
    var
        name: Text;
    begin
        name := Helper.GetProductNameMarketing();
        Assert.IsTrue(name.Contains('Business Central'), 'ProductName.Marketing() should contain Business Central');
    end;

    [Test]
    procedure CompanyPropertyDisplayNameReturnsText()
    var
        name: Text;
    begin
        name := Helper.GetCompanyPropertyDisplayName();
        Assert.AreEqual('My Company', name, 'CompanyProperty.DisplayName() should return stub company name');
    end;

    [Test]
    procedure CompanyPropertyUrlNameReturnsText()
    var
        name: Text;
    begin
        name := Helper.GetCompanyPropertyUrlName();
        Assert.AreEqual('My%20Company', name, 'CompanyProperty.UrlName() should return URL-encoded stub name');
    end;

    [Test]
    procedure LogMessageIsNoOp()
    begin
        // LogMessage is a no-op (telemetry not available without service tier)
        // Verify it executes without error
        Helper.CallLogMessage();
        Assert.IsTrue(true, 'Session.LogMessage should execute as no-op without error');
    end;

    [Test]
    procedure LogMessageWarningIsNoOp()
    begin
        Helper.CallLogMessageWarning();
        Assert.IsTrue(true, 'Session.LogMessage with Warning verbosity should execute as no-op');
    end;

    [Test]
    procedure RoundDateTimeNoArgsReturnsSameDateTime()
    var
        dt: DateTime;
        rounded: DateTime;
    begin
        dt := CreateDateTime(20240115D, 123456T);
        rounded := Helper.GetRoundedDateTime(dt);
        Assert.AreEqual(dt, rounded, 'RoundDateTime with no precision should return the same datetime');
    end;

    [Test]
    procedure RoundDateTimeWithPrecisionRoundsToNearestMinute()
    var
        dt: DateTime;
        rounded: DateTime;
        expected: DateTime;
    begin
        dt := CreateDateTime(20240115D, 123456T);
        rounded := Helper.GetRoundedDateTimePrecision(dt, 60000);
        expected := CreateDateTime(20240115D, 123500T);
        Assert.AreEqual(expected, rounded, 'RoundDateTime(dt, 60000) should round 12:34:56 to 12:35:00');
    end;

    [Test]
    procedure RoundDateTimeDirectionUpRoundsUp()
    var
        dt: DateTime;
        rounded: DateTime;
        expected: DateTime;
    begin
        dt := CreateDateTime(20240115D, 123456T);
        rounded := Helper.GetRoundedDateTimeDirection(dt, 60000, '>');
        expected := CreateDateTime(20240115D, 123500T);
        Assert.AreEqual(expected, rounded, 'RoundDateTime with > should round 12:34:56 up to 12:35:00');
    end;

    [Test]
    procedure RoundDateTimeDirectionDownRoundsDown()
    var
        dt: DateTime;
        rounded: DateTime;
        expected: DateTime;
    begin
        dt := CreateDateTime(20240115D, 123456T);
        rounded := Helper.GetRoundedDateTimeDirection(dt, 60000, '<');
        expected := CreateDateTime(20240115D, 123400T);
        Assert.AreEqual(expected, rounded, 'RoundDateTime with < should round 12:34:56 down to 12:34:00');
    end;

    [Test]
    procedure RoundDateTimeDirectionNearestRoundsToNearest()
    var
        dt: DateTime;
        rounded: DateTime;
        expected: DateTime;
    begin
        dt := CreateDateTime(20240115D, 123456T);
        rounded := Helper.GetRoundedDateTimeDirection(dt, 60000, '=');
        expected := CreateDateTime(20240115D, 123500T);
        Assert.AreEqual(expected, rounded, 'RoundDateTime with = should round 12:34:56 to nearest minute 12:35:00');
    end;

    [Test]
    procedure ApplicationAreaReturnsEmpty()
    var
        appArea: Text;
    begin
        appArea := Helper.GetApplicationArea();
        Assert.AreEqual('', appArea, 'Session.ApplicationArea() should return empty string in standalone mode');
    end;

    [Test]
    procedure LockTimeoutTrueIsNoOp()
    begin
        // LockTimeout is a no-op (no real database)
        Helper.CallLockTimeout(true);
        Assert.IsTrue(true, 'Database.LockTimeout(true) should execute as no-op');
    end;

    [Test]
    procedure LockTimeoutFalseIsNoOp()
    begin
        Helper.CallLockTimeout(false);
        Assert.IsTrue(true, 'Database.LockTimeout(false) should execute as no-op');
    end;

    [Test]
    procedure GetExecutionContextReturnsNormal()
    var
        ctx: ExecutionContext;
    begin
        ctx := Helper.GetExecutionContext();
        Assert.AreEqual(ExecutionContext::Normal, ctx, 'Session.GetExecutionContext should return Normal');
    end;

    [Test]
    procedure GetModuleExecutionContextReturnsNormal()
    var
        ctx: ExecutionContext;
    begin
        ctx := Helper.GetModuleExecutionContext();
        Assert.AreEqual(ExecutionContext::Normal, ctx, 'Session.GetModuleExecutionContext should return Normal');
    end;

    [Test]
    procedure NormalDateOnZeroDateReturnsZero()
    var
        d: Date;
    begin
        d := Helper.GetNormalDate(0D);
        Assert.AreEqual(0D, d, 'NormalDate(0D) should return 0D');
    end;
}
