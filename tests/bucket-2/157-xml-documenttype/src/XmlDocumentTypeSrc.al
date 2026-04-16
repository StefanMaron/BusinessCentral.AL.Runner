/// Helper codeunit exercising XmlDocumentType.Create and property getters.
/// Actual signatures: GetName(var Result: Text): Boolean, etc.
codeunit 61710 "XDT Helper"
{
    /// Create an XmlDocumentType with the given name and return its name via GetName.
    procedure CreateAndGetName(name: Text): Text
    var
        docType: XmlDocumentType;
        result: Text;
    begin
        docType := XmlDocumentType.Create(name);
        docType.GetName(result);
        exit(result);
    end;

    /// Create an XmlDocumentType with all four parameters and return its name.
    procedure CreateFull(name: Text; publicId: Text; systemId: Text; internalSubset: Text): Text
    var
        docType: XmlDocumentType;
        result: Text;
    begin
        docType := XmlDocumentType.Create(name, publicId, systemId, internalSubset);
        docType.GetName(result);
        exit(result);
    end;

    /// Proving helper — returns a+b+1 to verify the codeunit is live.
    procedure AddWithBonus(a: Integer; b: Integer): Integer
    begin
        exit(a + b + 1);
    end;
}
