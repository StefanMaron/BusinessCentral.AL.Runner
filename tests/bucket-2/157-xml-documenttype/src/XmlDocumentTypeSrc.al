/// Helper codeunit exercising XmlDocumentType methods in standalone mode.
/// Actual signatures: GetName(var Result: Text): Boolean, etc.
codeunit 61720 "XDT Helper"
{
    // ── Already-covered: Create + GetName ────────────────────────────────────

    procedure CreateAndGetName(name: Text): Text
    var
        docType: XmlDocumentType;
        result: Text;
    begin
        docType := XmlDocumentType.Create(name);
        docType.GetName(result);
        exit(result);
    end;

    procedure CreateFull(name: Text; publicId: Text; systemId: Text; internalSubset: Text): Text
    var
        docType: XmlDocumentType;
        result: Text;
    begin
        docType := XmlDocumentType.Create(name, publicId, systemId, internalSubset);
        docType.GetName(result);
        exit(result);
    end;

    procedure AddWithBonus(a: Integer; b: Integer): Integer
    begin
        exit(a + b + 1);
    end;

    // ── GetPublicId / GetSystemId / GetInternalSubset ─────────────────────────

    procedure GetPublicIdFromFull(name: Text; publicId: Text; systemId: Text; internalSubset: Text): Text
    var
        docType: XmlDocumentType;
        result: Text;
    begin
        docType := XmlDocumentType.Create(name, publicId, systemId, internalSubset);
        docType.GetPublicId(result);
        exit(result);
    end;

    procedure GetSystemIdFromFull(name: Text; publicId: Text; systemId: Text; internalSubset: Text): Text
    var
        docType: XmlDocumentType;
        result: Text;
    begin
        docType := XmlDocumentType.Create(name, publicId, systemId, internalSubset);
        docType.GetSystemId(result);
        exit(result);
    end;

    procedure GetInternalSubsetFromFull(name: Text; publicId: Text; systemId: Text; internalSubset: Text): Text
    var
        docType: XmlDocumentType;
        result: Text;
    begin
        docType := XmlDocumentType.Create(name, publicId, systemId, internalSubset);
        docType.GetInternalSubset(result);
        exit(result);
    end;

    // ── SetPublicId / SetSystemId / SetInternalSubset / SetName ──────────────

    procedure SetPublicId_GetBack(publicId: Text): Text
    var
        docType: XmlDocumentType;
        result: Text;
    begin
        docType := XmlDocumentType.Create('html', '', '', '');
        docType.SetPublicId(publicId);
        docType.GetPublicId(result);
        exit(result);
    end;

    procedure SetSystemId_GetBack(systemId: Text): Text
    var
        docType: XmlDocumentType;
        result: Text;
    begin
        docType := XmlDocumentType.Create('html', '', '', '');
        docType.SetSystemId(systemId);
        docType.GetSystemId(result);
        exit(result);
    end;

    procedure SetInternalSubset_GetBack(internalSubset: Text): Text
    var
        docType: XmlDocumentType;
        result: Text;
    begin
        docType := XmlDocumentType.Create('html', '', '', '');
        docType.SetInternalSubset(internalSubset);
        docType.GetInternalSubset(result);
        exit(result);
    end;

    procedure SetName_GetBack(newName: Text): Text
    var
        docType: XmlDocumentType;
        result: Text;
    begin
        docType := XmlDocumentType.Create('html');
        docType.SetName(newName);
        docType.GetName(result);
        exit(result);
    end;

    // ── WriteTo ───────────────────────────────────────────────────────────────

    procedure WriteToText_NotEmpty(): Boolean
    var
        docType: XmlDocumentType;
        result: Text;
    begin
        docType := XmlDocumentType.Create('html', '', '', '');
        docType.WriteTo(result);
        exit(result <> '');
    end;

    procedure WriteToText_ContainsName(): Boolean
    var
        docType: XmlDocumentType;
        result: Text;
    begin
        docType := XmlDocumentType.Create('mytype', '', '', '');
        docType.WriteTo(result);
        exit(result.IndexOf('mytype') > 0);
    end;

    // ── AsXmlNode ─────────────────────────────────────────────────────────────
    // AsXmlNode returns an XmlNode; verify it can be round-tripped to Text.

    procedure AsXmlNode_WritesToText(): Boolean
    var
        docType: XmlDocumentType;
        node: XmlNode;
        result: Text;
    begin
        docType := XmlDocumentType.Create('html');
        node := docType.AsXmlNode();
        node.WriteTo(result);
        exit(result <> '');
    end;

}
