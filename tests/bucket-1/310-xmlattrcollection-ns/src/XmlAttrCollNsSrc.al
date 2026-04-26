/// Helper codeunit exercising namespace-qualified XmlAttributeCollection overloads:
///   Get(Text, XmlAttribute)              — Get by local name (text overload)
///   Get(Text, Text, XmlAttribute)        — Get by namespaceURI + localName
///   Remove(XmlAttribute)                 — Remove by attribute reference
///   Remove(Text, Text)                   — Remove by namespaceURI + localName
///   Set(Text, Text, Text)                — Set namespaceURI + localName + value
/// All are BC-native methods on NavXmlAttributeCollection. Issue #1376.
codeunit 310000 "XACNS Src"
{
    // ── Get(localName: Text, var attr: XmlAttribute) ──────────────────────────
    // Text overload — retrieve by attribute name string.

    /// Build element with attribute named localName; retrieve by text name.
    procedure Get_ByLocalName_Value(localName: Text): Text
    var
        el: XmlElement;
        attr: XmlAttribute;
    begin
        el := XmlElement.Create('root');
        el.SetAttribute(localName, 'hello');
        if el.Attributes().Get(localName, attr) then
            exit(attr.Value);
        exit('');
    end;

    /// Returns false when the named attribute does not exist (text overload).
    procedure Get_ByLocalName_Missing(): Boolean
    var
        el: XmlElement;
        attr: XmlAttribute;
    begin
        el := XmlElement.Create('root');
        el.SetAttribute('other', '1');
        exit(el.Attributes().Get('missing', attr));
    end;

    /// Distinct values for distinct attribute names — proves Get is name-sensitive.
    procedure Get_ByLocalName_TwoAttrs_First(): Text
    var
        el: XmlElement;
        attr: XmlAttribute;
    begin
        el := XmlElement.Create('root');
        el.SetAttribute('a', 'one');
        el.SetAttribute('b', 'two');
        if el.Attributes().Get('a', attr) then
            exit(attr.Value);
        exit('');
    end;

    procedure Get_ByLocalName_TwoAttrs_Second(): Text
    var
        el: XmlElement;
        attr: XmlAttribute;
    begin
        el := XmlElement.Create('root');
        el.SetAttribute('a', 'one');
        el.SetAttribute('b', 'two');
        if el.Attributes().Get('b', attr) then
            exit(attr.Value);
        exit('');
    end;

    // ── Get(namespaceURI: Text, localName: Text, var attr: XmlAttribute) ──────
    // BC maps this to NavXmlAttributeCollection.ALGet(errorLevel, localName, namespaceUri, result).

    /// Add a namespace-qualified attribute via SetAttribute(name, ns, value),
    /// then retrieve it using Get(namespaceURI, localName).
    procedure Get_ByNamespace_Value(namespaceURI: Text; localName: Text): Text
    var
        el: XmlElement;
        attr: XmlAttribute;
    begin
        el := XmlElement.Create('root');
        // XmlElement.SetAttribute(Text, Text, Text) = ALSetAttribute(localName, ns, value)
        el.SetAttribute(localName, namespaceURI, 'nsval');
        if el.Attributes().Get(namespaceURI, localName, attr) then
            exit(attr.Value);
        exit('');
    end;

    /// Returns false when no attribute with that namespace exists.
    procedure Get_ByNamespace_Missing(): Boolean
    var
        el: XmlElement;
        attr: XmlAttribute;
    begin
        el := XmlElement.Create('root');
        exit(el.Attributes().Get('http://missing', 'x', attr));
    end;

    // ── Remove(attr: XmlAttribute) ────────────────────────────────────────────

    /// Add two attributes; remove first by reference; remaining count must be 1.
    procedure Remove_ByRef_CountAfter(): Integer
    var
        el: XmlElement;
        a1: XmlAttribute;
        a2: XmlAttribute;
    begin
        el := XmlElement.Create('root');
        a1 := XmlAttribute.Create('color', 'blue');
        a2 := XmlAttribute.Create('size', '42');
        el.Add(a1);
        el.Add(a2);
        el.Attributes().Remove(a1);
        exit(el.Attributes().Count());
    end;

    /// After Remove(attr), Get(name) returns false.
    procedure Remove_ByRef_GetAfterRemove(): Boolean
    var
        el: XmlElement;
        attr: XmlAttribute;
        dummy: XmlAttribute;
    begin
        el := XmlElement.Create('root');
        attr := XmlAttribute.Create('id', '7');
        el.Add(attr);
        el.Attributes().Remove(attr);
        exit(el.Attributes().Get('id', dummy));
    end;

    // ── Remove(namespaceURI: Text, localName: Text) ───────────────────────────

    /// Add namespace-qualified attribute; remove by NS + local name; count = 0.
    procedure Remove_ByNs_Count(): Integer
    var
        el: XmlElement;
    begin
        el := XmlElement.Create('root');
        el.SetAttribute('kind', 'http://ns', 'v');
        el.Attributes().Remove('http://ns', 'kind');
        exit(el.Attributes().Count());
    end;

    /// After Remove(ns, name), Get(ns, name) returns false.
    procedure Remove_ByNs_GetAfterRemove(): Boolean
    var
        el: XmlElement;
        attr: XmlAttribute;
    begin
        el := XmlElement.Create('root');
        el.SetAttribute('kind', 'http://ns', 'v');
        el.Attributes().Remove('http://ns', 'kind');
        exit(el.Attributes().Get('http://ns', 'kind', attr));
    end;

    // ── Set(namespaceURI: Text, localName: Text, value: Text) ─────────────────

    /// Set a namespace-qualified attribute; retrieve it via Get(ns, name).
    procedure Set_ByNs_GetValue(namespaceURI: Text; localName: Text; value: Text): Text
    var
        el: XmlElement;
        attr: XmlAttribute;
    begin
        el := XmlElement.Create('root');
        el.Attributes().Set(namespaceURI, localName, value);
        if el.Attributes().Get(namespaceURI, localName, attr) then
            exit(attr.Value);
        exit('');
    end;

    /// Set overwrites an existing namespace-qualified attribute.
    procedure Set_ByNs_Overwrite(): Text
    var
        el: XmlElement;
        attr: XmlAttribute;
    begin
        el := XmlElement.Create('root');
        el.SetAttribute('color', 'http://ns', 'old');
        el.Attributes().Set('http://ns', 'color', 'new');
        if el.Attributes().Get('http://ns', 'color', attr) then
            exit(attr.Value);
        exit('');
    end;
}
