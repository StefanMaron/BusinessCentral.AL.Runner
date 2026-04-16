codeunit 61711 "XDT XmlDocumentType Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure Create_WithName_ReturnsCorrectName()
    var
        Helper: Codeunit "XDT Helper";
    begin
        // Positive: XmlDocumentType.Create('html') must return name 'html'.
        Assert.AreEqual('html', Helper.CreateAndGetName('html'),
            'XmlDocumentType.Create(''html'').GetName() must return ''html''');
    end;

    [Test]
    procedure Create_WithDifferentName_ReturnsCorrectName()
    var
        Helper: Codeunit "XDT Helper";
    begin
        // Positive: XmlDocumentType.Create('svg') must return name 'svg'.
        Assert.AreEqual('svg', Helper.CreateAndGetName('svg'),
            'XmlDocumentType.Create(''svg'').GetName() must return ''svg''');
    end;

    [Test]
    procedure Create_NameNotMismatch()
    var
        Helper: Codeunit "XDT Helper";
    begin
        // Negative: GetName must not return a different name (guards against constant stub).
        Assert.AreNotEqual('other', Helper.CreateAndGetName('html'),
            'XmlDocumentType.GetName() must return the actual name, not a constant');
    end;

    [Test]
    procedure CreateFull_WithAllParams_ReturnsCorrectName()
    var
        Helper: Codeunit "XDT Helper";
    begin
        // Positive: 4-param Create must also set name correctly.
        Assert.AreEqual('html', Helper.CreateFull('html', '-//W3C//DTD HTML 4.01//EN',
            'http://www.w3.org/TR/html4/strict.dtd', ''),
            'XmlDocumentType.Create(name, pub, sys, sub).GetName() must return name');
    end;

    [Test]
    procedure AddWithBonus_ProvingCompilationUnitLive()
    var
        Helper: Codeunit "XDT Helper";
    begin
        // Proving: the codeunit is live — real computation returns a+b+1.
        Assert.AreEqual(8, Helper.AddWithBonus(3, 4), 'AddWithBonus(3,4) must return 3+4+1=8');
        Assert.AreEqual(1, Helper.AddWithBonus(0, 0), 'AddWithBonus(0,0) must return 0+0+1=1');
    end;

    [Test]
    procedure AddWithBonus_NotPlainSum()
    var
        Helper: Codeunit "XDT Helper";
    begin
        // Negative: AddWithBonus must NOT return a plain sum (no-op trap guard).
        Assert.AreNotEqual(7, Helper.AddWithBonus(3, 4), 'AddWithBonus must not just return a+b');
    end;
}
