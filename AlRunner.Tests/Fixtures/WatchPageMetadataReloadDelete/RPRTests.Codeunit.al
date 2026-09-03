/// <summary>
/// Never edited between cycles — its own PASS on both cycles is only used to confirm each
/// --watch cycle actually finished; the real claim WatchPageMetadataReloadDeleteTests checks
/// is the AL_RUNNER_TRACE_PAGE_METADATA=1 stderr trace, not this test's result.
/// </summary>
codeunit 70203 "RPR Tests"
{
    Subtype = Test;

    [Test]
    procedure CycleCompletes()
    begin
        // Deliberately empty — an AL [Test] with no body always passes; see this codeunit's
        // header comment for what this is actually standing in for.
    end;
}
