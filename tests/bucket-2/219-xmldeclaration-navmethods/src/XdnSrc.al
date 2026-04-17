/// Helper codeunit exercising XmlDeclaration navigation and tree-manipulation
/// methods (issue #779):
///   WriteTo, AsXmlNode, GetParent, GetDocument,
///   Remove, AddAfterSelf, AddBeforeSelf, ReplaceWith,
///   SelectNodes, SelectSingleNode.
codeunit 97907 "XDN Src"
{
    // ── WriteTo ──────────────────────────────────────────────────────────────────

    /// Serializes the declaration to a Text string.
    procedure WriteToText(Version: Text; Encoding: Text; Standalone: Text): Text
    var
        Decl: XmlDeclaration;
        Result: Text;
    begin
        Decl := XmlDeclaration.Create(Version, Encoding, Standalone);
        Decl.WriteTo(Result);
        exit(Result);
    end;

    // ── AsXmlNode ─────────────────────────────────────────────────────────────

    /// Returns an XmlNode wrapping the declaration.
    procedure CreateAsXmlNode(Version: Text): XmlNode
    var
        Decl: XmlDeclaration;
    begin
        Decl := XmlDeclaration.Create(Version, '', '');
        exit(Decl.AsXmlNode());
    end;

    // ── GetParent ─────────────────────────────────────────────────────────────

    /// Returns false when the declaration is not attached to a document.
    procedure GetParentDetached(): Boolean
    var
        Decl: XmlDeclaration;
        Parent: XmlElement;
    begin
        Decl := XmlDeclaration.Create('1.0', '', '');
        exit(Decl.GetParent(Parent));
    end;

    // ── GetDocument ───────────────────────────────────────────────────────────

    /// Returns false when the declaration is detached.
    procedure GetDocumentDetached(): Boolean
    var
        Decl: XmlDeclaration;
        OutDoc: XmlDocument;
    begin
        Decl := XmlDeclaration.Create('1.0', '', '');
        exit(Decl.GetDocument(OutDoc));
    end;

    /// GetDocument returns false even when the declaration is set on a document
    /// via SetDeclaration — declarations are not stored as navigable child nodes.
    procedure GetDocumentSetDeclaration(): Boolean
    var
        Doc: XmlDocument;
        Decl: XmlDeclaration;
        OutDoc: XmlDocument;
    begin
        Doc := XmlDocument.Create();
        Decl := XmlDeclaration.Create('1.0', 'utf-8', '');
        Doc.SetDeclaration(Decl);
        exit(Decl.GetDocument(OutDoc));
    end;

    // ── Remove ────────────────────────────────────────────────────────────────

    /// After Remove() the document no longer has a declaration.
    procedure RemoveFromDoc(): Boolean
    var
        Doc: XmlDocument;
        Decl: XmlDeclaration;
        OutDecl: XmlDeclaration;
    begin
        Doc := XmlDocument.Create();
        Decl := XmlDeclaration.Create('1.0', '', '');
        Doc.SetDeclaration(Decl);
        Decl.Remove();
        exit(Doc.GetDeclaration(OutDecl));
    end;

    // ── AddAfterSelf / AddBeforeSelf ──────────────────────────────────────────

    /// Calls AddAfterSelf on a declaration — no-op since declarations have no
    /// XmlElement parent in the standard node tree.
    procedure AddAfterSelfNoThrow(): Boolean
    var
        Decl: XmlDeclaration;
        Sibling: XmlDeclaration;
    begin
        Decl := XmlDeclaration.Create('1.0', '', '');
        Sibling := XmlDeclaration.Create('1.1', '', '');
        Decl.AddAfterSelf(Sibling);
        exit(true);
    end;

    procedure AddBeforeSelfNoThrow(): Boolean
    var
        Decl: XmlDeclaration;
        Sibling: XmlDeclaration;
    begin
        Decl := XmlDeclaration.Create('1.0', '', '');
        Sibling := XmlDeclaration.Create('1.1', '', '');
        Decl.AddBeforeSelf(Sibling);
        exit(true);
    end;

    // ── ReplaceWith ───────────────────────────────────────────────────────────

    procedure ReplaceWithNoError(): Boolean
    var
        Doc: XmlDocument;
        Root: XmlElement;
        Decl: XmlDeclaration;
        Replacement: XmlDeclaration;
    begin
        Doc := XmlDocument.Create();
        Root := XmlElement.Create('root');
        Doc.Add(Root);
        Decl := XmlDeclaration.Create('1.0', '', '');
        Doc.SetDeclaration(Decl);
        Replacement := XmlDeclaration.Create('1.1', '', '');
        Decl.ReplaceWith(Replacement);
        exit(true);
    end;

    // ── SelectNodes ───────────────────────────────────────────────────────────

    /// Returns 0 — declarations have no child nodes.
    procedure SelectNodesCount(): Integer
    var
        Decl: XmlDeclaration;
        NodeList: XmlNodeList;
    begin
        Decl := XmlDeclaration.Create('1.0', '', '');
        Decl.SelectNodes('*', NodeList);
        exit(NodeList.Count());
    end;

    // ── SelectSingleNode ──────────────────────────────────────────────────────

    procedure SelectSingleNodeNotFound(): Boolean
    var
        Decl: XmlDeclaration;
        Result: XmlNode;
    begin
        Decl := XmlDeclaration.Create('1.0', '', '');
        exit(Decl.SelectSingleNode('*', Result));
    end;
}
