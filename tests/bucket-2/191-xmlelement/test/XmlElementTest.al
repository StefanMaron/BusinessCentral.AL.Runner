codeunit 60171 "XEL Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "XEL Src";

    // --- Create + Name ---

    [Test]
    procedure Create_Name_Matches()
    begin
        Assert.AreEqual('root', Src.Create_Name(),
            'XmlElement.Create(name).Name must return the given name');
    end;

    [Test]
    procedure LocalName_Matches()
    begin
        Assert.AreEqual('book', Src.LocalName(),
            'XmlElement.LocalName must return the element name');
    end;

    // --- InnerXml ---

    [Test]
    procedure Create_InnerXml_ContainsChild()
    var
        inner: Text;
    begin
        inner := Src.Create_And_InnerXml();
        Assert.IsTrue(inner.Contains('<item'),
            'InnerXml after Add must contain the child element');
    end;

    [Test]
    procedure Create_InnerXml_ContainsAttribute()
    var
        inner: Text;
    begin
        inner := Src.Create_And_InnerXml();
        Assert.IsTrue(inner.Contains('id="1"'),
            'InnerXml must contain attribute id="1"');
    end;

    // --- SetAttribute + Attributes ---

    [Test]
    procedure SetAttribute_Read()
    begin
        Assert.AreEqual('blue', Src.Create_SetAttribute_Read(),
            'SetAttribute value must be readable via Attributes().Get');
    end;

    [Test]
    procedure HasAttributes_True_After_SetAttribute()
    begin
        Assert.IsTrue(Src.HasAttributes_After_Set(),
            'HasAttributes must be true after SetAttribute');
    end;

    [Test]
    procedure HasAttributes_False_Initially()
    begin
        Assert.IsFalse(Src.HasAttributes_NoAttributes_False(),
            'HasAttributes must be false on a fresh element');
    end;

    [Test]
    procedure RemoveAttribute_ClearsHasAttributes()
    begin
        Assert.IsFalse(Src.RemoveAttribute_Gone(),
            'HasAttributes must be false after the only attribute is removed');
    end;

    // --- HasElements ---

    [Test]
    procedure HasElements_True_AfterAdd()
    begin
        Assert.IsTrue(Src.HasElements_AfterAdd(),
            'HasElements must be true after Add');
    end;

    [Test]
    procedure HasElements_False_Empty()
    begin
        Assert.IsFalse(Src.HasElements_Empty_False(),
            'HasElements must be false on a fresh element');
    end;

    // --- GetChildElements ---

    [Test]
    procedure GetChildElements_Count2()
    begin
        Assert.AreEqual(2, Src.GetChildElementCount(),
            'GetChildElements.Count must reflect the number of added children');
    end;

    // --- InnerText ---

    [Test]
    procedure InnerText_AfterAddText()
    begin
        Assert.AreEqual('hello world', Src.InnerText_FromAddText(),
            'InnerText must return the text added to the element');
    end;

    // --- SelectNodes ---

    [Test]
    procedure SelectNodes_CountsDescendants()
    begin
        Assert.AreEqual(2, Src.SelectNodesCount(),
            'SelectNodes(//item) must return both item children');
    end;

    // --- AsXmlNode ---

    [Test]
    procedure AsXmlNode_RoundTrip_PreservesName()
    begin
        Assert.AreEqual('widget', Src.AsXmlNode_NameMatches(),
            'AsXmlNode().AsXmlElement().Name must equal the original name');
    end;

    // --- Negative traps ---

    [Test]
    procedure HasAttributes_DifferentBefore_After()
    begin
        // Negative trap: HasAttributes must actually reflect state.
        Assert.AreNotEqual(Src.HasAttributes_NoAttributes_False(),
            Src.HasAttributes_After_Set(),
            'HasAttributes must differ before and after SetAttribute');
    end;

    [Test]
    procedure HasElements_DifferentBefore_After()
    begin
        Assert.AreNotEqual(Src.HasElements_Empty_False(),
            Src.HasElements_AfterAdd(),
            'HasElements must differ before and after Add');
    end;
}
