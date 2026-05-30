/// <summary>
/// Interface whose single method dereferences the implementation codeunit's
/// instance var-record field. This mirrors BaseApp's "Price Source" interface
/// whose GetId reads Codeunit7035.vendor (a Vendor record instance field).
/// </summary>
interface "IState Provider ICS"
{
    /// <summary>
    /// Returns a name derived from the implementation's own instance
    /// var-record field. NREs if that field's record handle has been disposed.
    /// </summary>
    procedure GetProbedName(): Text;
}
