/// <summary>
/// A second test library, between the first library and the tests. "Wrap" calls the first library's
/// "Calc", so a test reaches the generated member through two bundles.
/// </summary>
codeunit 65470 "Lib Bundle Wrapper"
{
    procedure Wrap(): Integer
    var
        Helper: Codeunit "Lib Bundle Helper";
    begin
        exit(Helper.Calc());
    end;
}
