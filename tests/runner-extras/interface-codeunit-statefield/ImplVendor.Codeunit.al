/// <summary>
/// Implementation of "IState Provider ICS". It owns a global (instance)
/// var-record field `Probe`. The AL compiler allocates `Probe` (a
/// NavRecordHandle for table 60200) inside the codeunit's emitted private
/// InitializeComponent(), which runs only from the codeunit constructor.
///
/// When AL casts `Enum::"State Kind ICS"::Vendor` to the interface, the runner's
/// ALCompiler.ToInterface(NavOption) builds this codeunit and wraps it in a
/// NavInterfaceHandle. Previously the runner disposed the building codeunit
/// handle afterwards, tearing down `Probe`'s handle tree; the later
/// GetProbedName() interface dispatch then read a disposed `Probe` -> NRE.
/// </summary>
codeunit 60201 "Iface Impl Vendor ICS" implements "IState Provider ICS"
{
    var
        Probe: Record "State Rec ICS";

    procedure GetProbedName(): Text
    begin
        // Dereference the instance var-record field. If its handle was disposed
        // by the ToInterface path, this access NREs (the bug under test).
        Probe."No." := 'PROBE';
        Probe."Name" := 'alive';
        exit(Probe."Name");
    end;
}
