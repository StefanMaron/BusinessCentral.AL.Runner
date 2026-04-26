/// Exercises remaining XmlElement methods: GetChildNodes, GetParent,
/// GetDocument, AddFirst, Remove, RemoveNodes, WriteTo.
codeunit 60310 "XEX Src"
{
    procedure GetChildNodeCount(): Integer
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
        exit(root.GetChildNodes().Count());
    end;

    procedure GetParent_Returns_True(): Boolean
    var
        root: XmlElement;
        child: XmlElement;
        parent: XmlElement;
    begin
        root := XmlElement.Create('root');
        child := XmlElement.Create('inner');
        root.Add(child);
        exit(child.GetParent(parent));
    end;

    procedure AddFirst_BecomesFirstChild(): Text
    var
        root: XmlElement;
        first: XmlElement;
        second: XmlElement;
        nodes: XmlNodeList;
        n: XmlNode;
    begin
        root := XmlElement.Create('root');
        second := XmlElement.Create('second');
        root.Add(second);
        first := XmlElement.Create('first');
        root.AddFirst(first);
        nodes := root.GetChildNodes();
        nodes.Get(1, n);
        exit(n.AsXmlElement().Name);
    end;

    procedure Remove_DecreasesChildCount(): Integer
    var
        root: XmlElement;
        child: XmlElement;
    begin
        root := XmlElement.Create('root');
        child := XmlElement.Create('child');
        root.Add(child);
        child.Remove();
        exit(root.GetChildNodes().Count());
    end;

    procedure RemoveNodes_ClearsAll(): Integer
    var
        root: XmlElement;
        a: XmlElement;
        b: XmlElement;
    begin
        root := XmlElement.Create('root');
        a := XmlElement.Create('a');
        b := XmlElement.Create('b');
        root.Add(a);
        root.Add(b);
        root.RemoveNodes();
        exit(root.GetChildNodes().Count());
    end;

    procedure WriteTo_ContainsElementName(): Boolean
    var
        el: XmlElement;
        outText: Text;
    begin
        el := XmlElement.Create('widget');
        el.SetAttribute('id', '1');
        el.WriteTo(outText);
        exit(outText.Contains('widget') and outText.Contains('id="1"'));
    end;
}
