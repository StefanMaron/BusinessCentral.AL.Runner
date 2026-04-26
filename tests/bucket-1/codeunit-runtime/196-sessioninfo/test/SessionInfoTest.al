codeunit 60231 "SI Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "SI Src";

    [Test]
    procedure SqlRowsRead_ReturnsZero()
    begin
        // Standalone contract — no real SQL, so the counter is always 0.
        Assert.AreEqual(0, Src.GetSqlRowsRead(),
            'SessionInformation.SqlRowsRead must return 0 in standalone mode');
    end;

    [Test]
    procedure SqlStatementsExecuted_ReturnsZero()
    begin
        Assert.AreEqual(0, Src.GetSqlStatementsExecuted(),
            'SessionInformation.SqlStatementsExecuted must return 0 in standalone mode');
    end;

    [Test]
    procedure Callstack_DoesNotThrow()
    var
        cs: Text;
    begin
        // Callstack may return empty or a stack trace; just verify it doesn't crash.
        cs := Src.GetCallstack();
        Assert.IsTrue(true, 'SessionInformation.Callstack must not throw');
    end;
}
