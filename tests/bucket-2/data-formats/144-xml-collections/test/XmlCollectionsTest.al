codeunit 59371 "XC Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "XC Src";

    [Test]
    procedure XmlNodeList_Count_NoChildren()
    begin
        Assert.AreEqual(0, Src.GetChildElementCount_Empty(),
            'XmlElement with 0 children must have GetChildNodes.Count = 0');
    end;

    [Test]
    procedure XmlNodeList_Count_OneChild()
    begin
        Assert.AreEqual(1, Src.GetChildElementCount_OneChild(),
            'XmlElement with 1 child must have GetChildNodes.Count = 1');
    end;

    [Test]
    procedure XmlNodeList_Count_ThreeChildren()
    begin
        Assert.AreEqual(3, Src.GetChildElementCount_Three(),
            'XmlElement with 3 children must have GetChildNodes.Count = 3');
    end;

    [Test]
    procedure XmlNodeList_Count_NotFixed_NegativeTrap()
    begin
        // Negative: guard against a stub that always returns the same value.
        Assert.AreNotEqual(
            Src.GetChildElementCount_Empty(),
            Src.GetChildElementCount_Three(),
            'Count must differ for elements with different child counts');
    end;

    [Test]
    procedure XmlAttributeCollection_Count_NoAttributes()
    begin
        Assert.AreEqual(0, Src.GetAttributeCount_Zero(),
            'XmlElement with 0 attributes must have Attributes.Count = 0');
    end;

    [Test]
    procedure XmlAttributeCollection_Count_TwoAttributes()
    begin
        Assert.AreEqual(2, Src.GetAttributeCount_Two(),
            'XmlElement with 2 attributes must have Attributes.Count = 2');
    end;

    [Test]
    procedure XmlAttributeCollection_Count_ThreeAttributes()
    begin
        Assert.AreEqual(3, Src.GetAttributeCount_Three(),
            'XmlElement with 3 attributes must have Attributes.Count = 3');
    end;

    [Test]
    procedure XmlAttributeCollection_Count_NotFixed_NegativeTrap()
    begin
        // Negative: guard against a stub that always returns the same value.
        Assert.AreNotEqual(
            Src.GetAttributeCount_Zero(),
            Src.GetAttributeCount_Two(),
            'Attributes.Count must differ for elements with different attribute counts');
    end;

    [Test]
    procedure XmlElement_IsEmpty_TrueForEmpty()
    begin
        Assert.IsTrue(Src.IsElementEmpty_NoChildren(),
            'XmlElement with no children must report IsEmpty = true');
    end;

    [Test]
    procedure XmlElement_IsEmpty_FalseForWithChild()
    begin
        Assert.IsFalse(Src.IsElementEmpty_WithChild(),
            'XmlElement with a child must report IsEmpty = false');
    end;
}
