/// <summary>
/// Cross-assembly duplicate-subscriber dispatch. The dep's "XED Subscriber"
/// codeunit type is present in two loaded assemblies here; firing the event
/// must invoke the real AL subscriber EXACTLY once — no TargetException
/// ('Object does not match target type'), no double-fire.
/// </summary>
codeunit 61220 "XED Main Tests"
{
    Subtype = Test;

    [Test]
    procedure FirePing_CrossAssemblyDuplicateSubscriber_RunsExactlyOnce()
    var
        Publisher: Codeunit "XED Publisher";
        Count: Integer;
    begin
        Count := 0;
        Publisher.FirePing(Count);
        if Count <> 1 then
            Error('Expected subscriber to fire exactly once across duplicate assemblies, got %1 increment(s).', Count);
    end;
}
