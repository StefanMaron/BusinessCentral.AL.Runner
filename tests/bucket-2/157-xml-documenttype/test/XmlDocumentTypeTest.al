codeunit 61721 "XDT XmlDocumentType Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        H: Codeunit "XDT Helper";

    // ── Already-covered: Create + GetName ─────────────────────────────────────

    [Test]
    procedure Create_WithName_ReturnsCorrectName()
    begin
        Assert.AreEqual('html', H.CreateAndGetName('html'),
            'XmlDocumentType.Create(''html'').GetName() must return ''html''');
    end;

    [Test]
    procedure Create_WithDifferentName_ReturnsCorrectName()
    begin
        Assert.AreEqual('svg', H.CreateAndGetName('svg'),
            'XmlDocumentType.Create(''svg'').GetName() must return ''svg''');
    end;

    [Test]
    procedure Create_NameNotMismatch()
    begin
        Assert.AreNotEqual('other', H.CreateAndGetName('html'),
            'XmlDocumentType.GetName() must return the actual name, not a constant');
    end;

    [Test]
    procedure CreateFull_WithAllParams_ReturnsCorrectName()
    begin
        Assert.AreEqual('html', H.CreateFull('html', '-//W3C//DTD HTML 4.01//EN',
            'http://www.w3.org/TR/html4/strict.dtd', ''),
            'XmlDocumentType.Create(name, pub, sys, sub).GetName() must return name');
    end;

    [Test]
    procedure AddWithBonus_ProvingCompilationUnitLive()
    begin
        Assert.AreEqual(8, H.AddWithBonus(3, 4), 'AddWithBonus(3,4) must return 3+4+1=8');
        Assert.AreEqual(1, H.AddWithBonus(0, 0), 'AddWithBonus(0,0) must return 0+0+1=1');
    end;

    [Test]
    procedure AddWithBonus_NotPlainSum()
    begin
        Assert.AreNotEqual(7, H.AddWithBonus(3, 4), 'AddWithBonus must not just return a+b');
    end;

    // ── GetPublicId ───────────────────────────────────────────────────────────

    [Test]
    procedure GetPublicId_ReturnsStoredValue()
    begin
        Assert.AreEqual('-//W3C//DTD HTML 4.01//EN',
            H.GetPublicIdFromFull('html', '-//W3C//DTD HTML 4.01//EN', '', ''),
            'GetPublicId must return the publicId passed to Create');
    end;

    [Test]
    procedure GetPublicId_EmptyWhenNotSet()
    begin
        Assert.AreEqual('', H.GetPublicIdFromFull('html', '', '', ''),
            'GetPublicId must return empty when publicId was empty');
    end;

    [Test]
    procedure GetPublicId_NotMismatch()
    begin
        Assert.AreNotEqual('wrong',
            H.GetPublicIdFromFull('html', '-//W3C//DTD HTML 4.01//EN', '', ''),
            'GetPublicId must not return a wrong constant');
    end;

    // ── GetSystemId ───────────────────────────────────────────────────────────

    [Test]
    procedure GetSystemId_ReturnsStoredValue()
    begin
        Assert.AreEqual('http://www.w3.org/TR/html4/strict.dtd',
            H.GetSystemIdFromFull('html', '', 'http://www.w3.org/TR/html4/strict.dtd', ''),
            'GetSystemId must return the systemId passed to Create');
    end;

    [Test]
    procedure GetSystemId_EmptyWhenNotSet()
    begin
        Assert.AreEqual('', H.GetSystemIdFromFull('html', '', '', ''),
            'GetSystemId must return empty when systemId was empty');
    end;

    // ── GetInternalSubset ─────────────────────────────────────────────────────

    [Test]
    procedure GetInternalSubset_ReturnsStoredValue()
    begin
        Assert.AreEqual('<!ELEMENT foo EMPTY>',
            H.GetInternalSubsetFromFull('html', '', '', '<!ELEMENT foo EMPTY>'),
            'GetInternalSubset must return the internalSubset passed to Create');
    end;

    [Test]
    procedure GetInternalSubset_EmptyWhenNotSet()
    begin
        Assert.AreEqual('', H.GetInternalSubsetFromFull('html', '', '', ''),
            'GetInternalSubset must return empty when internalSubset was empty');
    end;

    // ── SetPublicId ───────────────────────────────────────────────────────────

    [Test]
    procedure SetPublicId_UpdatesValue()
    begin
        Assert.AreEqual('-//W3C//DTD HTML 4.01//EN',
            H.SetPublicId_GetBack('-//W3C//DTD HTML 4.01//EN'),
            'SetPublicId then GetPublicId must return the new value');
    end;

    [Test]
    procedure SetPublicId_DistinctFromInitial()
    begin
        Assert.AreNotEqual('', H.SetPublicId_GetBack('-//W3C//DTD HTML 4.01//EN'),
            'SetPublicId must produce a non-empty result when a non-empty id is set');
    end;

    // ── SetSystemId ───────────────────────────────────────────────────────────

    [Test]
    procedure SetSystemId_UpdatesValue()
    begin
        Assert.AreEqual('http://example.org/dtd',
            H.SetSystemId_GetBack('http://example.org/dtd'),
            'SetSystemId then GetSystemId must return the new value');
    end;

    // ── SetInternalSubset ─────────────────────────────────────────────────────

    [Test]
    procedure SetInternalSubset_UpdatesValue()
    begin
        Assert.AreEqual('<!ELEMENT bar EMPTY>',
            H.SetInternalSubset_GetBack('<!ELEMENT bar EMPTY>'),
            'SetInternalSubset then GetInternalSubset must return the new value');
    end;

    // ── SetName ───────────────────────────────────────────────────────────────

    [Test]
    procedure SetName_UpdatesValue()
    begin
        Assert.AreEqual('svg', H.SetName_GetBack('svg'),
            'SetName then GetName must return the new name');
    end;

    [Test]
    procedure SetName_DistinctFromOriginal()
    begin
        Assert.AreNotEqual('html', H.SetName_GetBack('svg'),
            'SetName must change the name from the original html');
    end;

}
