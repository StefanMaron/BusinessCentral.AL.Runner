/// <summary>
/// Bundle-1 test: proves the publisher/subscriber pair works within a single
/// assembly AND ensures this bundle's emitted assembly is loaded (and its
/// subscriber registered) before bundle 2 loads the SAME app again as a
/// source-compiled dependency assembly.
/// </summary>
codeunit 61212 "XED Dep Tests"
{
    Subtype = Test;

    [Test]
    procedure FirePing_SameAssembly_SubscriberRunsExactlyOnce()
    var
        Publisher: Codeunit "XED Publisher";
        Count: Integer;
    begin
        Count := 0;
        Publisher.FirePing(Count);
        if Count <> 1 then
            Error('Expected subscriber to fire exactly once, got %1 increment(s).', Count);
    end;
}
