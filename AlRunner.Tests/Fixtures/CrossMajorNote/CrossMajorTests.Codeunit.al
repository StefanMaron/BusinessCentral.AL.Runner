/// <summary>
/// Declares BC major 1 (see app.json) — guaranteed to differ from whatever major the
/// runner engine under test was actually built for (27-29 range), so the cross-major note
/// (issue #2210) is deterministic on every host/CI leg. The single test must PASS: the
/// whole point of #2210 is that this mismatch does not need to refuse the run.
/// </summary>
codeunit 60950 "Cross Major Note Fixture"
{
    Subtype = Test;

    [Test]
    procedure CrossMajorNote_MismatchedDeclaredMajor_StillRunsAndPasses()
    var
        Sum: Integer;
    begin
        Sum := 2 + 2;
        if Sum <> 4 then
            Error('cross-major mismatch must not prevent this suite from compiling and running');
    end;
}
