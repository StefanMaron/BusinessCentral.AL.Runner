codeunit 70890 "SUWP Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure SuwpSidIsTheRunnerGeneratedOneWhenNoRowMovedIt()
    var
        UserRec: Record User;
    begin
        // The row and Sid() are the same fact: both the generated SID (BcRuntime.SessionUserWindowsSid).
        UserRec.Get(UserSecurityId());
        if Database.Sid('') <> 'S-1-5-21-1413829460-1431520594-0-1000' then
            Error('Sid() must be the runner-generated Windows SID, but it is "%1"', Database.Sid(''));
        if UserRec."Windows Security ID" <> Database.Sid('') then
            Error('the session user row carries "%1" but Sid() answers "%2"', UserRec."Windows Security ID", Database.Sid(''));
    end;
}
