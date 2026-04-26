codeunit 60191 "XAT Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "XAT Src";

    [Test]
    procedure Create_And_Read_Value()
    begin
        Assert.AreEqual('42', Src.CreateAndReadValue(),
            'XmlAttribute.Create + Value must return the supplied value');
    end;

    [Test]
    procedure Create_And_Read_Name()
    begin
        Assert.AreEqual('id', Src.CreateAndReadName(),
            'XmlAttribute.Name must return the supplied name');
    end;

    [Test]
    procedure Element_Attribute_RoundTrip_Value()
    begin
        // Via SetAttribute("color","blue") + Attributes().Get, the attribute
        // value round-trips.
        Assert.AreEqual('blue', Src.ElementAttributeRoundTrip(),
            'Attribute value read via Attributes().Get must match the SetAttribute value');
    end;

    [Test]
    procedure LocalName_Matches()
    begin
        Assert.AreEqual('id', Src.AttrLocalName(),
            'XmlAttribute.LocalName must equal the attribute name for non-namespaced attrs');
    end;

    [Test]
    procedure NamespaceUri_Default_Empty()
    begin
        Assert.AreEqual('', Src.AttrNamespaceUri_Default(),
            'XmlAttribute.NamespaceUri must default to empty string');
    end;

    [Test]
    procedure AsXmlNode_RoundTrip()
    begin
        Assert.AreEqual('id', Src.AttrAsXmlNode_LocalNameMatches(),
            'AsXmlNode().AsXmlAttribute().LocalName must equal the original LocalName');
    end;

    [Test]
    procedure ReplaceValue_Via_SetAttribute()
    begin
        // SetAttribute with an existing name must update the value (no duplicate attr).
        Assert.AreEqual('red', Src.ReplaceAttributeValue_Via_SetAttribute(),
            'SetAttribute must replace the value when the attribute already exists');
    end;

    [Test]
    procedure Name_And_Value_Are_Different_NegativeTrap()
    begin
        // Negative trap: Name and Value must come from different slots.
        Assert.IsFalse(Src.AttributeValue_Not_AttributeName_NegativeTrap(),
            'XmlAttribute.Name and XmlAttribute.Value must not alias');
    end;

    // ── XmlAttribute.Create(LocalName, NamespaceUri, Value) ──────────────────

    [Test]
    procedure Create_3Arg_Value()
    begin
        Assert.AreEqual('978-0-13', Src.Create3Arg_Value(),
            'Create(localName, ns, value).Value must return the third argument');
    end;

    [Test]
    procedure Create_3Arg_LocalName()
    begin
        Assert.AreEqual('isbn', Src.Create3Arg_LocalName(),
            'Create(localName, ns, value).LocalName must return the first argument');
    end;

    [Test]
    procedure Create_3Arg_NamespaceUri()
    begin
        Assert.AreEqual('urn:books:1', Src.Create3Arg_NamespaceUri(),
            'Create(localName, ns, value).NamespaceUri must return the second argument (with '':'')');
    end;

    [Test]
    procedure Create_3Arg_EmptyNamespace_BehavesAsNoNamespace()
    begin
        Assert.AreEqual('', Src.Create3Arg_EmptyNamespace_NamespaceUri(),
            'Create(localName, '''', value) must round-trip an empty NamespaceUri');
    end;

    [Test]
    procedure Create_3Arg_NamespaceUri_NotAliasingValue_NegativeTrap()
    begin
        Assert.IsFalse(Src.Create3Arg_NamespaceUri_Aliases_Value_NegativeTrap(),
            'XmlAttribute.NamespaceUri and XmlAttribute.Value must not alias');
    end;
}
