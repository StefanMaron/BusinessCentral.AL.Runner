/// Helper codeunit that wraps the file-based XmlPort.Import overload so the
/// test can exercise it without calling it inline.
/// Actual static signature: XmlPort.Import(PortNumber: Integer; FileName: Text)
codeunit 61800 "XIF Helper"
{
    /// Call XmlPort.Import(portNumber, fileName) — must be a no-op stub
    /// in standalone mode (no actual file I/O performed).
    procedure CallImportFile(portNumber: Integer; fileName: Text)
    begin
        XmlPort.Import(portNumber, fileName);
    end;

    /// Proving helper — returns a+b+1 to verify the codeunit is live.
    procedure AddWithBonus(a: Integer; b: Integer): Integer
    begin
        exit(a + b + 1);
    end;
}
