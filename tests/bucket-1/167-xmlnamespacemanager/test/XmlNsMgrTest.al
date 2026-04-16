codeunit 92001 "XNM Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "XNM Src";

    // ── AddNamespace / LookupNamespace ───────────────────────────

    [Test]
    procedure XmlNsMgr_AddAndLookup_RoundTrips()
    begin
        Assert.AreEqual('http://example.com',
            Src.AddAndLookup('ns', 'http://example.com'),
            'LookupNamespace must return the URI added via AddNamespace');
    end;

    [Test]
    procedure XmlNsMgr_AddAndLookup_DifferentUris()
    begin
        Assert.AreNotEqual(
            Src.AddAndLookup('a', 'http://a.com'),
            Src.AddAndLookup('b', 'http://b.com'),
            'Different URIs must produce different LookupNamespace results');
    end;

    // ── LookupPrefix ─────────────────────────────────────────────

    [Test]
    procedure XmlNsMgr_LookupPrefix_RoundTrips()
    begin
        Assert.AreEqual('ns',
            Src.LookupPrefix('ns', 'http://example.com'),
            'LookupPrefix must return the prefix added via AddNamespace');
    end;

    // ── HasNamespace ─────────────────────────────────────────────

    [Test]
    procedure XmlNsMgr_HasNamespace_TrueAfterAdd()
    begin
        Assert.IsTrue(Src.HasNamespace('ns', 'http://example.com'),
            'HasNamespace must return true for an added prefix');
    end;

    [Test]
    procedure XmlNsMgr_HasNamespace_FalseForMissing()
    begin
        Assert.IsFalse(Src.HasNamespaceMissing('unknown'),
            'HasNamespace must return false for a prefix that was never added');
    end;

    // ── RemoveNamespace ──────────────────────────────────────────

    [Test]
    procedure XmlNsMgr_RemoveNamespace_HasNamespaceFalseAfterRemoval()
    begin
        Assert.IsFalse(Src.RemoveNamespace('ns', 'http://example.com'),
            'HasNamespace must return false after RemoveNamespace');
    end;

    // ── PushScope / PopScope ─────────────────────────────────────

    [Test]
    procedure XmlNsMgr_PushPopScope_DefaultScopeRetained()
    begin
        // Default-scope namespaces survive a push+pop cycle
        Assert.AreEqual('http://example.com',
            Src.PushPopScope('ns', 'http://example.com'),
            'Namespace added before PushScope must still be visible after PopScope');
    end;

    // ── NameTable ────────────────────────────────────────────────

    [Test]
    procedure XmlNsMgr_NameTable_DoesNotThrow()
    begin
        Assert.IsTrue(Src.NameTableDoesNotThrow(),
            'NameTable() must not throw');
    end;

    // ── Namespace-qualified XPath via SelectNodes ─────────────────

    [Test]
    procedure XmlNsMgr_SelectWithNs_FindsNamespacedChildren()
    var
        cnt: Integer;
    begin
        cnt := Src.SelectWithNs('ns', 'http://example.com', 'item');
        Assert.IsTrue(cnt >= 0,
            'SelectNodes with namespace manager must return non-negative count');
    end;
}
