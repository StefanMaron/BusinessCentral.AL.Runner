/// Exercises the static XmlPort.Run call.
/// XmlPort.Run requires the BC service tier; it should throw a clear
/// 'XmlPort' error so the developer knows to abstract the dependency.
codeunit 60200 "XmlPort Run Logic"
{
    /// Calls the static form XmlPort.Run(portId) — must throw NotSupportedException.
    procedure TryStaticRun()
    begin
        XmlPort.Run(XmlPort::"XmlPort Run Items");
    end;

    /// Calls the static form XmlPort.Run(portId, isImport) — must throw NotSupportedException.
    procedure TryStaticRunWithDirection(IsImport: Boolean)
    begin
        XmlPort.Run(XmlPort::"XmlPort Run Items", IsImport);
    end;

    /// Calls the static form XmlPort.Run(portId, isImport, rec) — must throw NotSupportedException.
    procedure TryStaticRunWithRecord(IsImport: Boolean; var Rec: Record "XmlPort Run Item")
    begin
        XmlPort.Run(XmlPort::"XmlPort Run Items", IsImport, Rec);
    end;

    /// Returns a value that doesn't invoke XmlPort.Run — proves the codeunit compiles.
    procedure GetStatus(): Text
    begin
        exit('ready');
    end;
}
