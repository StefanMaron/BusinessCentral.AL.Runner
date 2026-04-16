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

    procedure AsXmlNode_IsXmlDocumentType(): Boolean
    var
        docType: XmlDocumentType;
        node: XmlNode;
    begin
        docType := XmlDocumentType.Create('html');
        node := docType.AsXmlNode();
        exit(node.IsXmlDocumentType());
    end;

    // ── GetDocument / GetParent (after adding to XmlDocument) ─────────────────

    procedure GetDocument_AfterAdd_ReturnsTrue(): Boolean
    var
        doc: XmlDocument;
        docType: XmlDocumentType;
        resultDoc: XmlDocument;
    begin
        XmlDocument.ReadFrom('<!DOCTYPE html><html/>', doc);
        if doc.GetDocumentType(docType) then
            exit(docType.GetDocument(resultDoc));
        exit(false);
    end;

    procedure GetParent_AfterAdd_ReturnsTrue(): Boolean
    var
        doc: XmlDocument;
        docType: XmlDocumentType;
        parentNode: XmlNode;
    begin
        XmlDocument.ReadFrom('<!DOCTYPE html><html/>', doc);
        if doc.GetDocumentType(docType) then
            exit(docType.GetParent(parentNode));
        exit(false);
    end;

    // ── Remove ────────────────────────────────────────────────────────────────

    procedure Remove_DoesNotError(): Boolean
    var
        doc: XmlDocument;
        docType: XmlDocumentType;
    begin
        XmlDocument.ReadFrom('<!DOCTYPE html><html/>', doc);
        if doc.GetDocumentType(docType) then
            docType.Remove();
        exit(true);
    end;

    // ── SelectNodes / SelectSingleNode ────────────────────────────────────────

    procedure SelectNodes_DoesNotError(): Boolean
    var
        docType: XmlDocumentType;
        nodeList: XmlNodeList;
    begin
        docType := XmlDocumentType.Create('html');
        docType.SelectNodes('*', nodeList);
        exit(true);
    end;

    procedure SelectSingleNode_DoesNotError(): Boolean
    var
        docType: XmlDocumentType;
        node: XmlNode;
    begin
        docType := XmlDocumentType.Create('html');
        docType.SelectSingleNode('*', node);
        exit(true);
    end;

    // ── AddAfterSelf / AddBeforeSelf / ReplaceWith ────────────────────────────
    // These require the doctype to be attached to a document. Test via XML parse.

    procedure AddAfterSelf_DoesNotError(): Boolean
    var
        doc: XmlDocument;
        docType: XmlDocumentType;
        elem: XmlElement;
    begin
        XmlDocument.ReadFrom('<!DOCTYPE html><html/>', doc);
        if doc.GetDocumentType(docType) then begin
            elem := XmlElement.Create('added');
            docType.AddAfterSelf(elem);
        end;
        exit(true);
    end;

    procedure AddBeforeSelf_DoesNotError(): Boolean
    var
        doc: XmlDocument;
        docType: XmlDocumentType;
        elem: XmlElement;
    begin
        XmlDocument.ReadFrom('<!DOCTYPE html><html/>', doc);
        if doc.GetDocumentType(docType) then begin
            elem := XmlElement.Create('before');
            docType.AddBeforeSelf(elem);
        end;
        exit(true);
    end;

    procedure ReplaceWith_DoesNotError(): Boolean
    var
        doc: XmlDocument;
        docType: XmlDocumentType;
        elem: XmlElement;
    begin
        XmlDocument.ReadFrom('<!DOCTYPE html><html/>', doc);
        if doc.GetDocumentType(docType) then begin
            elem := XmlElement.Create('replacement');
            docType.ReplaceWith(elem);
        end;
        exit(true);
    end;
}
