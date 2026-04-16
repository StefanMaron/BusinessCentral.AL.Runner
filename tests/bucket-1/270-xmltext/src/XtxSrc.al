/// Helper codeunit exercising XmlText methods.
codeunit 84403 "XTX Src"
{
    // ── Create / Value ──────────────────────────────────────────────────────────
    procedure CreateAndGetValue(txt: Text): Text
    var
        t: XmlText;
    begin
        t := XmlText.Create(txt);
        exit(t.Value());
    end;

    procedure SetAndGetValue(initial: Text; newVal: Text): Text
    var
        t: XmlText;
    begin
        t := XmlText.Create(initial);
        t.Value(newVal);
        exit(t.Value());
    end;

    // ── AsXmlNode ───────────────────────────────────────────────────────────────
    procedure CreateAsXmlNode(txt: Text): XmlNode
    var
        t: XmlText;
    begin
        t := XmlText.Create(txt);
        exit(t.AsXmlNode());
    end;

    // ── WriteTo ─────────────────────────────────────────────────────────────────
    procedure WriteToText(txt: Text): Text
    var
        t: XmlText;
        sb: TextBuilder;
    begin
        t := XmlText.Create(txt);
        t.WriteTo(sb);
        exit(sb.ToText());
    end;

    // ── AttachToElement / GetParent ─────────────────────────────────────────────
    procedure AttachToElement(txt: Text): Integer
    var
        root: XmlElement;
        t: XmlText;
        nodes: XmlNodeList;
    begin
        root := XmlElement.Create('root');
        t := XmlText.Create(txt);
        root.Add(t);
        nodes := root.GetChildNodes();
        exit(nodes.Count);
    end;

    procedure GetParentName(txt: Text): Text
    var
        root: XmlElement;
        t: XmlText;
        parent: XmlElement;
    begin
        root := XmlElement.Create('root');
        t := XmlText.Create(txt);
        root.Add(t);
        t.GetParent(parent);
        exit(parent.Name);
    end;

    // ── GetDocument ─────────────────────────────────────────────────────────────
    procedure GetDocumentSuccess(txt: Text): Boolean
    var
        doc: XmlDocument;
        root: XmlElement;
        t: XmlText;
        docOut: XmlDocument;
    begin
        root := XmlElement.Create('root');
        t := XmlText.Create(txt);
        root.Add(t);
        doc := XmlDocument.Create();
        doc.Add(root);
        exit(t.GetDocument(docOut));
    end;

    // ── SelectNodes ─────────────────────────────────────────────────────────────
    procedure SelectNodesCount(txt: Text; xPath: Text): Integer
    var
        doc: XmlDocument;
        root: XmlElement;
        t: XmlText;
        nodes: XmlNodeList;
    begin
        root := XmlElement.Create('root');
        t := XmlText.Create(txt);
        root.Add(t);
        doc := XmlDocument.Create();
        doc.Add(root);
        t.SelectNodes(xPath, nodes);
        exit(nodes.Count);
    end;

    // ── SelectSingleNode ────────────────────────────────────────────────────────
    procedure SelectSingleNodeFound(txt: Text; xPath: Text): Boolean
    var
        doc: XmlDocument;
        root: XmlElement;
        t: XmlText;
        node: XmlNode;
    begin
        root := XmlElement.Create('root');
        t := XmlText.Create(txt);
        root.Add(t);
        doc := XmlDocument.Create();
        doc.Add(root);
        exit(t.SelectSingleNode(xPath, node));
    end;

    // ── AddAfterSelf / AddBeforeSelf ────────────────────────────────────────────
    procedure AddAfterSelfChildCount(txt: Text): Integer
    var
        root: XmlElement;
        t1: XmlText;
        t2: XmlText;
        nodes: XmlNodeList;
    begin
        root := XmlElement.Create('root');
        t1 := XmlText.Create(txt);
        root.Add(t1);
        t2 := XmlText.Create('sibling');
        t1.AddAfterSelf(t2.AsXmlNode());
        nodes := root.GetChildNodes();
        exit(nodes.Count);
    end;

    procedure AddBeforeSelfChildCount(txt: Text): Integer
    var
        root: XmlElement;
        t1: XmlText;
        t2: XmlText;
        nodes: XmlNodeList;
    begin
        root := XmlElement.Create('root');
        t1 := XmlText.Create(txt);
        root.Add(t1);
        t2 := XmlText.Create('predecessor');
        t1.AddBeforeSelf(t2.AsXmlNode());
        nodes := root.GetChildNodes();
        exit(nodes.Count);
    end;

    // ── Remove ──────────────────────────────────────────────────────────────────
    procedure RemoveAndCountChildren(txt: Text): Integer
    var
        root: XmlElement;
        t: XmlText;
        nodes: XmlNodeList;
    begin
        root := XmlElement.Create('root');
        t := XmlText.Create(txt);
        root.Add(t);
        t.Remove();
        nodes := root.GetChildNodes();
        exit(nodes.Count);
    end;

    // ── ReplaceWith ─────────────────────────────────────────────────────────────
    procedure ReplaceWithComment(txt: Text; commentText: Text): Integer
    var
        root: XmlElement;
        t: XmlText;
        c: XmlComment;
        nodes: XmlNodeList;
    begin
        root := XmlElement.Create('root');
        t := XmlText.Create(txt);
        root.Add(t);
        c := XmlComment.Create(commentText);
        t.ReplaceWith(c.AsXmlNode());
        nodes := root.GetChildNodes();
        exit(nodes.Count);
    end;
}
