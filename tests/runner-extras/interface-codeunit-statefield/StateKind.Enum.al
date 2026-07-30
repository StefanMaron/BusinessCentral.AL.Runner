/// <summary>
/// Enum that implements "IState Provider ICS". Casting an enum value to the
/// interface (the AL `Enum::... as interface` form) compiles to
/// ALCompiler.ToInterface(NavOption, interfaceIndex) — the exact runner path
/// that previously disposed the implementing codeunit handle.
/// </summary>
enum 63200 "State Kind ICS" implements "IState Provider ICS"
{
    Extensible = false;

    value(0; "Vendor")
    {
        Implementation = "IState Provider ICS" = "Iface Impl Vendor ICS";
    }
}
