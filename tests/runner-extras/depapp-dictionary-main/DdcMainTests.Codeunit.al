/// <summary>
/// Proves an AL Dictionary used inside a DEPENDENCY app works.
///
/// NavObjectDictionary&lt;TKey,TValue&gt;.get_Target lazily builds its backing
/// SharedNavObjectDictionary by chaining base.Tree.Session.Company.SharedObjects.
/// SharedObjects is null on the headless skeleton, so BC's real body throws
/// ArgumentNullException (Parameter 'parent') on the first touch of the dictionary.
/// The runner has a replacement helper, but it is installed per CLOSED
/// instantiation, discovered by scanning a single assembly — the app under test.
/// DependencyLoader never registers dep assemblies, so dictionaries that live
/// inside a dependency are structurally invisible to that scan.
///
/// This suite was written to reproduce that and DOES NOT: all four cases passed
/// before the NavObjectDictionary Cecil rewrite landed. A SOURCE-COMPILED
/// dependency like the sibling app here works fine; Pageworks' failing dependency
/// is a PRECOMPILED .app. Whatever distinguishes the two was not isolated, so
/// nothing here should be read as coverage of the Pageworks cluster — the
/// evidence for that fix is the Pageworks measurement itself (+8, 0 regressions).
/// Kept as a regression pin for the dependency path that does work.
///
/// ValueTypeInDepApp is the decisive case. Reference-type instantiations share
/// one __Canon native body, so a hook installed for any unrelated ref/ref
/// dictionary covers them incidentally; value-type instantiations get their own
/// body and cannot be covered by accident.
/// </summary>
codeunit 61850 "DDC Main Tests"
{
    Subtype = Test;

    var
        DepApi: Codeunit "DDC Dep Api";

    [Test]
    procedure RefTypeDictionaryInDepApp_Works()
    begin
        // Dictionary of [Text, Text], entirely inside the dependency.
        if DepApi.RefTypeRoundTrip() <> '2|v2' then
            Error('Expected ''2|v2'' from the dependency''s ref-type dictionary, got ''%1''',
                DepApi.RefTypeRoundTrip());
    end;

    [Test]
    procedure ValueTypeDictionaryInDepApp_Works()
    begin
        // Dictionary of [Integer, Integer], entirely inside the dependency.
        // No __Canon sharing can rescue this one.
        if DepApi.ValueTypeRoundTrip() <> '2|200' then
            Error('Expected ''2|200'' from the dependency''s value-type dictionary, got ''%1''',
                DepApi.ValueTypeRoundTrip());
    end;

    [Test]
    procedure MixedKeyDictionaryInDepApp_Works()
    begin
        // Dictionary of [Integer, Text] — a third distinct instantiation, so a
        // fix that happened to cover one pair does not silently pass the others.
        if DepApi.MixedRoundTrip() <> '1|seven' then
            Error('Expected ''1|seven'' from the dependency''s mixed dictionary, got ''%1''',
                DepApi.MixedRoundTrip());
    end;

    [Test]
    procedure DepAppDictionaryIsNotASilentlyEmptyStandIn()
    begin
        // Negative direction: ContainsKey on an absent key must be false, and the
        // call must not throw. A stand-in that returned defaults for everything
        // would pass the positive tests above; it fails here.
        if DepApi.MissingKeyErrorText() <> 'ok' then
            Error('The dependency''s dictionary reported a key it never received: %1',
                DepApi.MissingKeyErrorText());
    end;
}
