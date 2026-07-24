/// <summary>
/// Regression pin for AL Dictionary semantics — and a recorded NEGATIVE result.
///
/// NavObjectDictionary&lt;TKey,TValue&gt;.get_Target lazily builds its backing
/// SharedNavObjectDictionary by chaining base.Tree.Session.Company.SharedObjects.
/// When that chain yields null, the real body passes null into
/// SharedNavObjectDictionary..ctor(ITreeSharedObjectContainer) and throws
/// ArgumentNullException (Parameter 'parent') on the first touch of the dictionary.
/// Pageworks hits exactly that, 7 tests, from a codeunit in a DEPENDENCY app.
///
/// These tests were written to reproduce it from a first-party test app. They DO NOT.
/// Every case below passes against BC's own unpatched get_Target — including the
/// method-local case, which the existing per-instantiation field/property scan could
/// never have discovered. So the session/company/SharedObjects chain is correctly wired
/// for codeunits in the app under test, and the Pageworks gap lies in whatever differs
/// about the dependency-app path, not in Dictionary support as such.
///
/// Kept because the negative result is worth locking in: if a later change to the
/// session/tree skeleton breaks the path that currently works, these fail loudly.
/// A pass here is NOT coverage of the Pageworks cluster.
/// </summary>
codeunit 61831 "DCT Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit "DCT Assert";
        GlobalDict: Dictionary of [Text, Integer];

    [Test]
    procedure DictAsMethodLocal_StoresAndRetrievesValues()
    var
        LocalDict: Dictionary of [Text, Integer];
        Got: Integer;
    begin
        // A Dictionary that exists ONLY as a method local — never a field, never a
        // property — so nothing can discover its closed instantiation by scanning types.
        LocalDict.Add('alpha', 11);
        LocalDict.Add('beta', 22);

        Assert.AreEqual(2, LocalDict.Count(), 'both entries should be present');

        LocalDict.Get('alpha', Got);
        Assert.AreEqual(11, Got, 'value stored under alpha');

        LocalDict.Get('beta', Got);
        Assert.AreEqual(22, Got, 'value stored under beta');

        Assert.IsTrue(LocalDict.ContainsKey('alpha'), 'alpha was added');
        Assert.IsFalse(LocalDict.ContainsKey('gamma'), 'gamma was never added');
    end;

    [Test]
    procedure DictAsMethodLocal_GetOnMissingKeyErrors()
    var
        LocalDict: Dictionary of [Text, Integer];
        Got: Integer;
    begin
        LocalDict.Add('alpha', 11);

        // Negative direction: the dictionary must behave like a real dictionary, not
        // like a silently-empty stand-in that returns a default for every key.
        asserterror LocalDict.Get('missing', Got);
        Assert.AreEqualText('The given key was not present in the dictionary.', GetLastErrorText(),
            'Get on an absent key must raise BC''s real key-not-found error');
    end;

    [Test]
    procedure DictAsMethodLocal_RemoveDropsTheEntry()
    var
        LocalDict: Dictionary of [Text, Integer];
    begin
        LocalDict.Add('alpha', 11);
        LocalDict.Add('beta', 22);

        Assert.IsTrue(LocalDict.Remove('alpha'), 'removing a present key reports true');
        Assert.AreEqual(1, LocalDict.Count(), 'one entry left after the remove');
        Assert.IsFalse(LocalDict.ContainsKey('alpha'), 'alpha is gone');
        Assert.IsTrue(LocalDict.ContainsKey('beta'), 'beta survived');

        Assert.IsFalse(LocalDict.Remove('alpha'), 'removing an absent key reports false');
    end;

    [Test]
    procedure DictAsGlobal_StoresAndRetrievesValues()
    var
        Got: Integer;
    begin
        // Control case: a Dictionary reachable as a FIELD. This is the shape the old
        // field/property scan could already discover, so it isolates "the helper works"
        // from "the helper is reachable for locals too".
        Clear(GlobalDict);
        GlobalDict.Add('one', 1);
        GlobalDict.Add('two', 2);

        Assert.AreEqual(2, GlobalDict.Count(), 'both entries should be present');

        GlobalDict.Get('two', Got);
        Assert.AreEqual(2, Got, 'value stored under two');
    end;

    [Test]
    procedure DictOfTextText_AlsoWorks()
    var
        TextDict: Dictionary of [Text, Text];
        Got: Text;
    begin
        // A different closed instantiation. A per-instantiation registration would have
        // to have found this one separately; a rewrite of the open generic gets it free.
        TextDict.Add('k', 'v');

        Assert.AreEqual(1, TextDict.Count(), 'one entry');
        TextDict.Get('k', Got);
        Assert.AreEqualText('v', Got, 'value stored under k');
    end;
}
