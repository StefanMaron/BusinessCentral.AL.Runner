/// Tests for namespace-qualified XmlAttributeCollection overloads (issue #1376):
///   Get(Text, XmlAttribute), Get(Text, Text, XmlAttribute),
///   Remove(XmlAttribute), Remove(Text, Text), Set(Text, Text, Text).
codeunit 310001 "XACNS Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "XACNS Src";

    // ── Get(Text, XmlAttribute) ───────────────────────────────────────────────

    [Test]
    procedure Get_ByLocalName_ReturnsValue()
    begin
        Assert.AreEqual('hello', Src.Get_ByLocalName_Value('id'),
            'Get(localName, attr) must return the attribute value');
    end;

    [Test]
    procedure Get_ByLocalName_Missing_ReturnsFalse()
    begin
        Assert.IsFalse(Src.Get_ByLocalName_Missing(),
            'Get(localName, attr) on a missing attribute must return false');
    end;

    [Test]
    procedure Get_ByLocalName_IsNameSensitive()
    begin
        // Negative trap: attributes a='one' and b='two' must return different values,
        // proving that Get dispatches on the actual name, not a constant.
        Assert.AreNotEqual(
            Src.Get_ByLocalName_TwoAttrs_First(),
            Src.Get_ByLocalName_TwoAttrs_Second(),
            'Get must return different values for different attribute names');
    end;

    [Test]
    procedure Get_ByLocalName_TwoAttrs_FirstValue()
    begin
        Assert.AreEqual('one', Src.Get_ByLocalName_TwoAttrs_First(),
            'Get(a) must return the value for attribute a');
    end;

    [Test]
    procedure Get_ByLocalName_TwoAttrs_SecondValue()
    begin
        Assert.AreEqual('two', Src.Get_ByLocalName_TwoAttrs_Second(),
            'Get(b) must return the value for attribute b');
    end;

    // ── Get(Text, Text, XmlAttribute) ─────────────────────────────────────────

    [Test]
    procedure Get_ByNamespace_ReturnsValue()
    begin
        Assert.AreEqual('nsval',
            Src.Get_ByNamespace_Value('http://example.com', 'color'),
            'Get(namespaceURI, localName, attr) must return the stored attribute value');
    end;

    [Test]
    procedure Get_ByNamespace_Missing_ReturnsFalse()
    begin
        Assert.IsFalse(Src.Get_ByNamespace_Missing(),
            'Get(ns, name) with no matching attribute must return false');
    end;

    // ── Remove(XmlAttribute) ─────────────────────────────────────────────────

    [Test]
    procedure Remove_ByRef_LeavesOtherAttributes()
    begin
        // Two attributes added; one removed by reference; one must remain.
        Assert.AreEqual(1, Src.Remove_ByRef_CountAfter(),
            'Remove(attr) must remove exactly the referenced attribute');
    end;

    [Test]
    procedure Remove_ByRef_GetReturnsFalse()
    begin
        Assert.IsFalse(Src.Remove_ByRef_GetAfterRemove(),
            'After Remove(attr), Get(name) must return false');
    end;

    // ── Remove(Text, Text) ────────────────────────────────────────────────────

    [Test]
    procedure Remove_ByNs_ClearsAttribute()
    begin
        Assert.AreEqual(0, Src.Remove_ByNs_Count(),
            'Remove(ns, name) must delete the namespace-qualified attribute');
    end;

    [Test]
    procedure Remove_ByNs_GetReturnsFalse()
    begin
        Assert.IsFalse(Src.Remove_ByNs_GetAfterRemove(),
            'After Remove(ns, name), Get(ns, name) must return false');
    end;

    // ── Set(Text, Text, Text) ─────────────────────────────────────────────────

    [Test]
    procedure Set_ByNs_StoresValue()
    begin
        Assert.AreEqual('green',
            Src.Set_ByNs_GetValue('http://example.com', 'color', 'green'),
            'Set(ns, name, value) must make the value retrievable via Get(ns, name)');
    end;

    [Test]
    procedure Set_ByNs_DifferentValues_DifferentResults()
    begin
        // Negative trap: Set stores the actual value, not a constant.
        Assert.AreNotEqual(
            Src.Set_ByNs_GetValue('http://example.com', 'color', 'green'),
            Src.Set_ByNs_GetValue('http://example.com', 'color', 'red'),
            'Set must store the supplied value, not a constant');
    end;

    [Test]
    procedure Set_ByNs_Overwrites_ExistingValue()
    begin
        Assert.AreEqual('new', Src.Set_ByNs_Overwrite(),
            'Set(ns, name, value) must overwrite an existing namespace-qualified attribute');
    end;
}
