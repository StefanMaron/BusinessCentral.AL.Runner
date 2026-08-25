/// <summary>
/// The "implementing app" codeunit half of the fixture. Deliberately does NOT declare a
/// CalcTotal procedure yet — TddBrokenProcTests.Codeunit.al calls it as if it already
/// existed.
/// </summary>
codeunit 65002 "Tdd Target Cu"
{
    procedure Placeholder()
    begin
    end;
}
