/// Exercises XmlAttributeCollection — Get, Set, Remove, RemoveAll.
codeunit 60240 "XAC Src"
{
    procedure GetAttrValue(attrName: Text): Text
    var
        el: XmlElement;
        attr: XmlAttribute;
    begin
        el := XmlElement.Create('root');
        el.SetAttribute('id', '1');
        el.SetAttribute('name', 'test');
        if el.Attributes().Get(attrName, attr) then
            exit(attr.Value);
        exit('');
    end;

    procedure GetMissing_ReturnsFalse(): Boolean
    var
        el: XmlElement;
        attr: XmlAttribute;
    begin
        el := XmlElement.Create('root');
        el.SetAttribute('id', '1');
        exit(el.Attributes().Get('missing', attr));
    end;

    procedure SetAttrUpdatesValue(): Text
    var
        el: XmlElement;
        attr: XmlAttribute;
    begin
        el := XmlElement.Create('root');
        el.SetAttribute('color', 'blue');
        // Attributes().Set(name, value) replaces or adds.
        el.Attributes().Set('color', 'red');
        if el.Attributes().Get('color', attr) then
            exit(attr.Value);
        exit('');
    end;

    procedure SetAttrAddsNew(): Boolean
    var
        el: XmlElement;
        attr: XmlAttribute;
    begin
        el := XmlElement.Create('root');
        el.Attributes().Set('size', '42');
        exit(el.Attributes().Get('size', attr));
    end;

    procedure RemoveAttr(): Boolean
    var
        el: XmlElement;
        attr: XmlAttribute;
    begin
        el := XmlElement.Create('root');
        el.SetAttribute('id', '1');
        el.SetAttribute('name', 'test');
        el.Attributes().Remove('id');
        exit(el.Attributes().Get('id', attr));
    end;

    procedure RemoveAll_ClearsAll(): Boolean
    var
        el: XmlElement;
    begin
        el := XmlElement.Create('root');
        el.SetAttribute('a', '1');
        el.SetAttribute('b', '2');
        el.RemoveAllAttributes();
        exit(el.HasAttributes());
    end;

    procedure Count_AfterTwoSets(): Integer
    var
        el: XmlElement;
    begin
        el := XmlElement.Create('root');
        el.SetAttribute('x', '1');
        el.SetAttribute('y', '2');
        exit(el.Attributes().Count());
    end;
}
