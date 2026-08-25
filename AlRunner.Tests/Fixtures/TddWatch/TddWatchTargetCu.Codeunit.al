/// <summary>
/// The "implementing app" half of this fixture. Deliberately does NOT declare a
/// DoubleIt procedure yet — TddWatchTests.Codeunit.al calls it as if it already
/// existed. WatchTests' edit step appends DoubleIt to this file in place, between
/// watch cycles, without restarting the process — the same file this test's edit
/// step targets.
/// </summary>
codeunit 65100 "Tdd Watch Target Cu"
{
    procedure Placeholder()
    begin
    end;
}
