/// Helper codeunit exercising XmlDocumentType.Create and property getters.
codeunit 61700 "XDT Helper"
{
    /// Create an XmlDocumentType with the given name and return its name.
    procedure CreateAndGetName(name: Text): Text
    var
        docType: XmlDocumentType;
    begin
        docType := XmlDocumentType.Create(name);
        exit(docType.GetName());
    end;

    /// Create an XmlDocumentType with all four parameters.
    procedure CreateFull(name: Text; publicId: Text; systemId: Text; internalSubset: Text): Text
    var
        docType: XmlDocumentType;
    begin
        docType := XmlDocumentType.Create(name, publicId, systemId, internalSubset);
        exit(docType.GetName());
    end;

    /// Proving helper — returns a+b+1 to verify the codeunit is live.
    procedure AddWithBonus(a: Integer; b: Integer): Integer
    begin
        exit(a + b + 1);
    end;
}
