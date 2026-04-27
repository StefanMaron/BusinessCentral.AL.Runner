codeunit 1316004 "StartSession Overloads Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure StartSession_3Arg_WithCompany_ReturnsTrue()
    var
        Api: Codeunit "StartSession Overloads Api";
        SessionId: Integer;
    begin
        // Positive: 3-arg StartSession(var SessionId, CodeunitID, Company) dispatches
        // synchronously and returns true; SessionId is set to a positive value.
        Assert.IsTrue(Api.StartWithCompany(SessionId), 'StartSession(3-arg) should return true');
        Assert.IsTrue(SessionId > 0, 'SessionId must be positive after 3-arg StartSession');
    end;

    [Test]
    procedure StartSession_4Arg_WithCompanyAndRecord_ReturnsTrue()
    var
        Api: Codeunit "StartSession Overloads Api";
        Rec: Record "StartSession Overloads Rec";
        SessionId: Integer;
    begin
        // Positive: 4-arg StartSession(var SessionId, CodeunitID, Company, Record)
        // dispatches synchronously and returns true.
        Rec.PK := 42;
        Rec.Value := 'TestValue';
        Rec.Insert();
        Assert.IsTrue(Api.StartWithCompanyAndRecord(SessionId, Rec), 'StartSession(4-arg) should return true');
        Assert.IsTrue(SessionId > 0, 'SessionId must be positive after 4-arg StartSession');
    end;

    [Test]
    procedure StartSession_5Arg_WithCompanyRecordAndTimeout_ReturnsTrue()
    var
        Api: Codeunit "StartSession Overloads Api";
        Rec: Record "StartSession Overloads Rec";
        SessionId: Integer;
    begin
        // Positive: 5-arg StartSession(var SessionId, CodeunitID, Company, Record, Timeout)
        // dispatches synchronously and returns true; timeout is ignored in the runner.
        Rec.PK := 99;
        Rec.Value := 'TimeoutTest';
        Rec.Insert();
        Assert.IsTrue(Api.StartWithCompanyRecordAndTimeout(SessionId, Rec), 'StartSession(5-arg) should return true');
        Assert.IsTrue(SessionId > 0, 'SessionId must be positive after 5-arg StartSession');
    end;
}
