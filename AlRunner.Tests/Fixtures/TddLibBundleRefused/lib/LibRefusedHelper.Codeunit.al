/// <summary>
/// A test library: the only object of this bundle. It calls "MissingByName" with a Text argument,
/// which --tdd refuses to generate (a Text parameter needs a length no call site fixes), so this
/// object cannot compile and the bundle is left with nothing to emit.
/// </summary>
codeunit 65480 "Lib Refused Helper"
{
    procedure Calc(): Integer
    var
        Loyalty: Codeunit "Lib Refused Loyalty";
        Result: Integer;
    begin
        Result := Loyalty.MissingByName('abc');
        exit(Result);
    end;
}
