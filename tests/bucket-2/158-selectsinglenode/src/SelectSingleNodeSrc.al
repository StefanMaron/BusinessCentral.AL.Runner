/// Helper codeunit exercising XmlDocument.SelectSingleNode / XmlElement.SelectSingleNode.
/// Builds XmlElement programmatically (avoiding XmlDocument.ReadFrom which hits a
/// separate rewriter gap, see #481).
codeunit 59690 "SSN Src"
{
    /// Build an element tree:
    /// <root>
    ///   <a id='1' />
    ///   <b id='2'>
    ///     <c id='3' />
    ///   </b>
    /// </root>
    procedure BuildRoot(): XmlElement
    var
        root: XmlElement;
        a: XmlElement;
        b: XmlElement;
        c: XmlElement;
    begin
        root := XmlElement.Create('root');

        a := XmlElement.Create('a');
        a.SetAttribute('id', '1');
        root.Add(a);

        b := XmlElement.Create('b');
        b.SetAttribute('id', '2');

        c := XmlElement.Create('c');
        c.SetAttribute('id', '3');
        b.Add(c);

        root.Add(b);

        exit(root);
    end;

    /// SelectSingleNode via an XmlElement receiver.
    procedure SelectByPath_FromElement(xpath: Text): Boolean
    var
        root: XmlElement;
        node: XmlNode;
    begin
        root := BuildRoot();
        exit(root.SelectSingleNode(xpath, node));
    end;

    /// Return the selected element's LocalName, or '' on no match.
    procedure SelectAndGetName(xpath: Text): Text
    var
        root: XmlElement;
        node: XmlNode;
    begin
        root := BuildRoot();
        if root.SelectSingleNode(xpath, node) then
            exit(node.AsXmlElement().LocalName)
        else
            exit('');
    end;
}
