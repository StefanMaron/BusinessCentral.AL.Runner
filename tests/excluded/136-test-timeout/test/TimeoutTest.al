codeunit 59961 "Timeout Test"
{
    Subtype = Test;

    var
        Assert: Codeunit "Library Assert";

    [Test]
    procedure InfiniteLoop_IsKilledByTimeout()
    var
        Looper: Codeunit "Infinite Looper";
    begin
        // [SCENARIO] A test that loops forever should be killed by --test-timeout
        // and surface a TimeoutException rather than hanging the runner.
        // Run with: dotnet run --project AlRunner -- --test-timeout 2 tests/excluded/136-test-timeout/src tests/excluded/136-test-timeout/test
        // The test itself is expected to FAIL with a timeout message.
        // This fixture validates that the runner does not hang.
        Looper.LoopForever();
        // If we reach here, the timeout did not fire — that is a bug.
        Assert.IsTrue(false, 'LoopForever should have been killed by timeout');
    end;
}
