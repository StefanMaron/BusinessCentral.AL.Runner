/// Helper codeunit exercising XmlAttribute — Create, Name, Value, NamespaceUri,
/// LocalName, AsXmlNode, IsDefault, plus attaching via XmlElement.SetAttribute.
codeunit 60190 "XAT Src"
{
    procedure CreateAndReadValue(): Text
    var
        attr: XmlAttribute;
    begin
        attr := XmlAttribute.Create('id', '42');
        exit(attr.Value);
    end;

    procedure CreateAndReadName(): Text
    var
        attr: XmlAttribute;
    begin
        attr := XmlAttribute.Create('id', '42');
        exit(attr.Name);
    end;

    procedure ElementAttributeRoundTrip(): Text
    var
        el: XmlElement;
        attr: XmlAttribute;
    begin
        el := XmlElement.Create('root');
        el.SetAttribute('color', 'blue');
        if el.Attributes().Get('color', attr) then
            exit(attr.Value);
        exit('');
    end;

    procedure AttrLocalName(): Text
    var
        attr: XmlAttribute;
    begin
        attr := XmlAttribute.Create('id', '42');
        exit(attr.LocalName);
    end;

    procedure AttrNamespaceUri_Default(): Text
    var
        attr: XmlAttribute;
    begin
        attr := XmlAttribute.Create('id', '42');
        exit(attr.NamespaceUri);
    end;

    procedure AttrAsXmlNode_LocalNameMatches(): Text
    var
        attr: XmlAttribute;
        n: XmlNode;
    begin
        attr := XmlAttribute.Create('id', '42');
        n := attr.AsXmlNode();
        // AsXmlNode round-trip — the node wraps the attribute and exposes
        // the same local name.
        exit(n.AsXmlAttribute().LocalName);
    end;

    procedure ReplaceAttributeValue_Via_SetAttribute(): Text
    var
        el: XmlElement;
        attr: XmlAttribute;
    begin
        el := XmlElement.Create('root');
        el.SetAttribute('color', 'blue');
        // Re-setting the same attribute updates the value (BC semantics).
        el.SetAttribute('color', 'red');
        if el.Attributes().Get('color', attr) then
            exit(attr.Value);
        exit('');
    end;

    procedure AttributeValue_Not_AttributeName_NegativeTrap(): Boolean
    var
        attr: XmlAttribute;
    begin
        // Negative trap: Name and Value must be different slots.
        attr := XmlAttribute.Create('id', '42');
        exit(attr.Name = attr.Value);
    end;

    // ── XmlAttribute.Create(LocalName, NamespaceUri, Value) ──────────────────

    procedure Create3Arg_Value(): Text
    var
        attr: XmlAttribute;
    begin
        attr := XmlAttribute.Create('isbn', 'urn:books:1', '978-0-13');
        exit(attr.Value);
    end;

    procedure Create3Arg_LocalName(): Text
    var
        attr: XmlAttribute;
    begin
        attr := XmlAttribute.Create('isbn', 'urn:books:1', '978-0-13');
        exit(attr.LocalName);
    end;

    procedure Create3Arg_NamespaceUri(): Text
    var
        attr: XmlAttribute;
    begin
        attr := XmlAttribute.Create('isbn', 'urn:books:1', '978-0-13');
        exit(attr.NamespaceUri);
    end;

    procedure Create3Arg_EmptyNamespace_NamespaceUri(): Text
    var
        attr: XmlAttribute;
    begin
        attr := XmlAttribute.Create('isbn', '', '978-0-13');
        exit(attr.NamespaceUri);
    end;

    procedure Create3Arg_NamespaceUri_Aliases_Value_NegativeTrap(): Boolean
    var
        attr: XmlAttribute;
    begin
        // Negative trap: NamespaceUri and Value must be different slots.
        attr := XmlAttribute.Create('isbn', 'urn:books:1', '978-0-13');
        exit(attr.NamespaceUri = attr.Value);
    end;
}
