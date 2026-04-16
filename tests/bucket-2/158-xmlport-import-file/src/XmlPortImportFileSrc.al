/// Helper codeunit that wraps the instance XmlPort.ImportFile so the
/// test can exercise the file-based import path without calling it inline.
/// Actual signature: myXmlPort.ImportFile([ErrorLevel: Boolean])
/// The filename is set via the XmlPort's FileName property, not a parameter.

xmlport 61800 "XIF XmlPort"
{
    Direction = Import;
    Format = Xml;
    schema
    {
        textelement(Root) { }
    }
}

codeunit 61801 "XIF Helper"
{
    /// Call xp.ImportFile() on an XmlPort instance — must be a no-op stub
    /// in standalone mode (no actual file I/O performed).
    procedure CallImportFile()
    var
        xp: XmlPort "XIF XmlPort";
    begin
        xp.ImportFile();
    end;

    /// Proving helper — returns a+b+1 to verify the codeunit is live.
    procedure AddWithBonus(a: Integer; b: Integer): Integer
    begin
        exit(a + b + 1);
    end;
}
