/// Helper codeunit exercising XmlComment.
codeunit 59780 "XCM Src"
{
    procedure CreateAndGetValue(text: Text): Text
    var
        c: XmlComment;
    begin
        c := XmlComment.Create(text);
        exit(c.Value);
    end;

    procedure CreateAsXmlNode(text: Text): XmlNode
    var
        c: XmlComment;
    begin
        c := XmlComment.Create(text);
        exit(c.AsXmlNode());
    end;

    procedure AttachToElement(text: Text): Integer
    var
        root: XmlElement;
        c: XmlComment;
        nodes: XmlNodeList;
    begin
        root := XmlElement.Create('root');
        c := XmlComment.Create(text);
        root.Add(c);

        nodes := root.GetChildNodes();
        exit(nodes.Count);
    end;
}
