/// Helper codeunit exercising XmlElement.RemoveAllAttributes().
/// Builds XmlElement programmatically (avoiding XmlDocument.ReadFrom which
/// hits a separate rewriter gap — see #481).
codeunit 59720 "RAA Src"
{
    procedure BuildWithThreeAttrs(): XmlElement
    var
        root: XmlElement;
    begin
        root := XmlElement.Create('root');
        root.SetAttribute('a', '1');
        root.SetAttribute('b', '2');
        root.SetAttribute('c', '3');
        exit(root);
    end;

    procedure AttrCount(elt: XmlElement): Integer
    var
        attrs: XmlAttributeCollection;
    begin
        attrs := elt.Attributes();
        exit(attrs.Count);
    end;

    procedure RemoveAll(elt: XmlElement)
    begin
        elt.RemoveAllAttributes();
    end;

    /// Build element, remove all attributes, return attribute count (expected 0).
    procedure RoundTripCount(): Integer
    var
        elt: XmlElement;
    begin
        elt := BuildWithThreeAttrs();
        elt.RemoveAllAttributes();
        exit(AttrCount(elt));
    end;

    /// Build with attrs + one child element, remove attributes, return child count.
    procedure PreservesChildren(): Integer
    var
        elt: XmlElement;
        child: XmlElement;
        kids: XmlNodeList;
    begin
        elt := XmlElement.Create('root');
        elt.SetAttribute('x', '9');
        child := XmlElement.Create('child');
        elt.Add(child);
        elt.RemoveAllAttributes();

        kids := elt.GetChildNodes();
        exit(kids.Count);
    end;

    /// Build, remove, return the element's LocalName (must not be affected).
    procedure PreservesName(): Text
    var
        elt: XmlElement;
    begin
        elt := BuildWithThreeAttrs();
        elt.RemoveAllAttributes();
        exit(elt.LocalName);
    end;
}
