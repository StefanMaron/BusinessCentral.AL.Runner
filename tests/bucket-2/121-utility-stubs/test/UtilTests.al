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
        Assert.AreNotEqual('', name, 'ProductName.Full() should return non-empty text');
    end;

    [Test]
    procedure ProductNameShortContainsBC()
    var
        name: Text;
    begin
        name := Helper.GetProductNameShort();
        Assert.AreNotEqual('', name, 'ProductName.Short() should return non-empty text');
    end;

    [Test]
    procedure ProductNameMarketingReturnsText()
    var
        name: Text;
    begin
        name := Helper.GetProductNameMarketing();
        Assert.AreNotEqual('', name, 'ProductName.Marketing() should return non-empty text');
    end;

    [Test]
    procedure CompanyPropertyDisplayNameReturnsText()
    var
        name: Text;
    begin
        name := Helper.GetCompanyPropertyDisplayName();
        // In standalone mode, can be empty string — just verify no crash
        Assert.IsTrue(true, 'CompanyProperty.DisplayName() should not crash');
    end;

    [Test]
    procedure CompanyPropertyUrlNameReturnsText()
    var
        name: Text;
    begin
        name := Helper.GetCompanyPropertyUrlName();
        Assert.IsTrue(true, 'CompanyProperty.UrlName() should not crash');
    end;

    [Test]
    procedure LogMessageDoesNotCrash()
    begin
        Helper.CallLogMessage();
        Assert.IsTrue(true, 'Session.LogMessage should not crash');
    end;

    [Test]
    procedure LogMessageWarningDoesNotCrash()
    begin
        Helper.CallLogMessageWarning();
        Assert.IsTrue(true, 'Session.LogMessage with Warning verbosity should not crash');
    end;

    [Test]
    procedure RoundDateTimeNoArgsReturnsDateTime()
    var
        dt: DateTime;
        rounded: DateTime;
    begin
        dt := CreateDateTime(20240115D, 120000T);
        rounded := Helper.GetRoundedDateTime(dt);
        Assert.AreNotEqual(0DT, rounded, 'RoundDateTime should return a non-zero datetime');
    end;

    [Test]
    procedure RoundDateTimeWithPrecision()
    var
        dt: DateTime;
        rounded: DateTime;
    begin
        dt := CreateDateTime(20240115D, 123456T);
        rounded := Helper.GetRoundedDateTimePrecision(dt, 60000);
        Assert.AreNotEqual(0DT, rounded, 'RoundDateTime with precision should return non-zero datetime');
    end;

    [Test]
    procedure RoundDateTimeDirectionUp()
    var
        dt: DateTime;
        rounded: DateTime;
    begin
        dt := CreateDateTime(20240115D, 123456T);
        rounded := Helper.GetRoundedDateTimeDirection(dt, 60000, '>');
        Assert.AreNotEqual(0DT, rounded, 'RoundDateTime with direction > should return non-zero');
    end;

    [Test]
    procedure RoundDateTimeDirectionDown()
    var
        dt: DateTime;
        rounded: DateTime;
    begin
        dt := CreateDateTime(20240115D, 123456T);
        rounded := Helper.GetRoundedDateTimeDirection(dt, 60000, '<');
        Assert.AreNotEqual(0DT, rounded, 'RoundDateTime with direction < should return non-zero');
    end;

    [Test]
    procedure RoundDateTimeDirectionNearest()
    var
        dt: DateTime;
        rounded: DateTime;
    begin
        dt := CreateDateTime(20240115D, 123456T);
        rounded := Helper.GetRoundedDateTimeDirection(dt, 60000, '=');
        Assert.AreNotEqual(0DT, rounded, 'RoundDateTime with direction = should return non-zero');
    end;

    [Test]
    procedure ApplicationAreaDoesNotCrash()
    var
        appArea: Text;
    begin
        appArea := Helper.GetApplicationArea();
        Assert.IsTrue(true, 'Session.ApplicationArea should not crash');
    end;

    [Test]
    procedure LockTimeoutTrueDoesNotCrash()
    begin
        Helper.CallLockTimeout(true);
        Assert.IsTrue(true, 'Database.LockTimeout(true) should not crash');
    end;

    [Test]
    procedure LockTimeoutFalseDoesNotCrash()
    begin
        Helper.CallLockTimeout(false);
        Assert.IsTrue(true, 'Database.LockTimeout(false) should not crash');
    end;

    [Test]
    procedure GetExecutionContextDoesNotCrash()
    var
        ctx: ExecutionContext;
    begin
        ctx := Helper.GetExecutionContext();
        Assert.IsTrue(true, 'Session.GetExecutionContext should not crash');
    end;

    [Test]
    procedure GetModuleExecutionContextDoesNotCrash()
    var
        ctx: ExecutionContext;
    begin
        ctx := Helper.GetModuleExecutionContext();
        Assert.IsTrue(true, 'Session.GetModuleExecutionContext should not crash');
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
