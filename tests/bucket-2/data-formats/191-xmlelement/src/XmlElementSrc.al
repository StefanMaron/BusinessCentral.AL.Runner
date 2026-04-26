/// Helper codeunit exercising XmlElement — creation, attributes, children,
/// query, and properties.
codeunit 60170 "XEL Src"
{
    procedure Create_Name(): Text
    var
        el: XmlElement;
    begin
        el := XmlElement.Create('root');
        exit(el.Name);
    end;

    procedure Create_And_InnerXml(): Text
    var
        root: XmlElement;
        child: XmlElement;
    begin
        root := XmlElement.Create('root');
        child := XmlElement.Create('item');
        child.SetAttribute('id', '1');
        root.Add(child);
        exit(root.InnerXml);
    end;

    procedure Create_SetAttribute_Read(): Text
    var
        el: XmlElement;
        attr: XmlAttribute;
        found: Boolean;
    begin
        el := XmlElement.Create('root');
        el.SetAttribute('color', 'blue');
        // Read back via Attributes().
        if el.Attributes().Get('color', attr) then
            exit(attr.Value);
        exit('');
    end;

    procedure HasAttributes_After_Set(): Boolean
    var
        el: XmlElement;
    begin
        el := XmlElement.Create('root');
        el.SetAttribute('id', '1');
        exit(el.HasAttributes());
    end;

    procedure HasAttributes_NoAttributes_False(): Boolean
    var
        el: XmlElement;
    begin
        el := XmlElement.Create('root');
        exit(el.HasAttributes());
    end;

    procedure HasElements_AfterAdd(): Boolean
    var
        root: XmlElement;
        child: XmlElement;
    begin
        root := XmlElement.Create('root');
        child := XmlElement.Create('item');
        root.Add(child);
        exit(root.HasElements());
    end;

    procedure HasElements_Empty_False(): Boolean
    var
        el: XmlElement;
    begin
        el := XmlElement.Create('root');
        exit(el.HasElements());
    end;

    procedure LocalName(): Text
    var
        el: XmlElement;
    begin
        el := XmlElement.Create('book');
        exit(el.LocalName);
    end;

    procedure GetChildElementCount(): Integer
    var
        root: XmlElement;
        a: XmlElement;
        b: XmlElement;
    begin
        root := XmlElement.Create('root');
        a := XmlElement.Create('child1');
        b := XmlElement.Create('child2');
        root.Add(a);
        root.Add(b);
        exit(root.GetChildElements().Count());
    end;

    procedure InnerText_FromAddText(): Text
    var
        el: XmlElement;
    begin
        el := XmlElement.Create('greeting');
        el.Add('hello world');
        exit(el.InnerText);
    end;

    procedure SelectNodesCount(): Integer
    var
        root: XmlElement;
        a: XmlElement;
        b: XmlElement;
        nodes: XmlNodeList;
    begin
        root := XmlElement.Create('root');
        a := XmlElement.Create('item');
        b := XmlElement.Create('item');
        root.Add(a);
        root.Add(b);
        root.SelectNodes('//item', nodes);
        exit(nodes.Count());
    end;

    procedure RemoveAttribute_Gone(): Boolean
    var
        el: XmlElement;
    begin
        el := XmlElement.Create('root');
        el.SetAttribute('id', '1');
        el.RemoveAttribute('id');
        exit(el.HasAttributes());
    end;

    procedure AsXmlNode_NameMatches(): Text
    var
        el: XmlElement;
        n: XmlNode;
    begin
        el := XmlElement.Create('widget');
        n := el.AsXmlNode();
        exit(n.AsXmlElement().Name);
    end;
}
