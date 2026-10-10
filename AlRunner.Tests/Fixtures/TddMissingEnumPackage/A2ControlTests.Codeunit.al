/// <summary>"Zulu Nowhere Status" is declared by nobody: generated, and it takes the FIRST free enum id although the
/// refused name sorts before it. It names no value: a value would need the repeat compile, which the refused AL0185 in the
/// other file takes down (#5447).</summary>
codeunit 65361 "Missing Enum Package Control"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure MissingEverywhere_IsGenerated()
    var
        Status: Enum "Zulu Nowhere Status";
    begin
        if Status.AsInteger() <> 0 then
            Error('a fresh enum is not 0');
    end;
}
