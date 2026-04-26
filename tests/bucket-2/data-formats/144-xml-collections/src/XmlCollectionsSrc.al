/// Helper codeunit exercising XmlNodeList.Count, XmlAttributeCollection.Count,
/// and XmlElement.IsEmpty — the surface issue #481 names.
/// Builds XmlElements programmatically (no XmlDocument.ReadFrom) so the test
/// does not depend on the XML-text-parser path.
codeunit 59370 "XC Src"
{
    procedure GetChildElementCount_Empty(): Integer
    var
        root: XmlElement;
        nodes: XmlNodeList;
    begin
        root := XmlElement.Create('root');
        nodes := root.GetChildNodes();
        exit(nodes.Count);
    end;

    procedure GetChildElementCount_OneChild(): Integer
    var
        root: XmlElement;
        child: XmlElement;
        nodes: XmlNodeList;
    begin
        root := XmlElement.Create('root');
        child := XmlElement.Create('child');
        root.Add(child);
        nodes := root.GetChildNodes();
        exit(nodes.Count);
    end;

    procedure GetChildElementCount_Three(): Integer
    var
        root: XmlElement;
        a: XmlElement;
        b: XmlElement;
        c: XmlElement;
        nodes: XmlNodeList;
    begin
        root := XmlElement.Create('root');
        a := XmlElement.Create('a');
        b := XmlElement.Create('b');
        c := XmlElement.Create('c');
        root.Add(a);
        root.Add(b);
        root.Add(c);
        nodes := root.GetChildNodes();
        exit(nodes.Count);
    end;

    procedure GetAttributeCount_Zero(): Integer
    var
        root: XmlElement;
        attrs: XmlAttributeCollection;
    begin
        root := XmlElement.Create('root');
        attrs := root.Attributes();
        exit(attrs.Count);
    end;

    procedure GetAttributeCount_Two(): Integer
    var
        root: XmlElement;
        attrs: XmlAttributeCollection;
    begin
        root := XmlElement.Create('root');
        root.SetAttribute('a', '1');
        root.SetAttribute('b', '2');
        attrs := root.Attributes();
        exit(attrs.Count);
    end;

    procedure GetAttributeCount_Three(): Integer
    var
        root: XmlElement;
        attrs: XmlAttributeCollection;
    begin
        root := XmlElement.Create('root');
        root.SetAttribute('id', 'x');
        root.SetAttribute('name', 'y');
        root.SetAttribute('kind', 'z');
        attrs := root.Attributes();
        exit(attrs.Count);
    end;

    procedure IsElementEmpty_NoChildren(): Boolean
    var
        root: XmlElement;
    begin
        root := XmlElement.Create('root');
        exit(root.IsEmpty);
    end;

    procedure IsElementEmpty_WithChild(): Boolean
    var
        root: XmlElement;
        child: XmlElement;
    begin
        root := XmlElement.Create('root');
        child := XmlElement.Create('child');
        root.Add(child);
        exit(root.IsEmpty);
    end;
}
