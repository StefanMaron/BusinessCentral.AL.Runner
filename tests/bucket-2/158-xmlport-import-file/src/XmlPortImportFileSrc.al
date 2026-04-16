/// Helper codeunit that wraps the instance XmlPort.ImportFile so the
/// test can exercise the file-based import path without calling it inline.
/// Actual signature: myXmlPort.ImportFile(FileName: Text)

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
    /// Call xp.ImportFile(fileName) on an XmlPort instance — must be a no-op stub
    /// in standalone mode (no actual file I/O performed).
    procedure CallImportFile(fileName: Text)
    var
        xp: XmlPort "XIF XmlPort";
    begin
        xp.ImportFile(fileName);
    end;

    /// Proving helper — returns a+b+1 to verify the codeunit is live.
    procedure AddWithBonus(a: Integer; b: Integer): Integer
    begin
        exit(a + b + 1);
    end;
}
