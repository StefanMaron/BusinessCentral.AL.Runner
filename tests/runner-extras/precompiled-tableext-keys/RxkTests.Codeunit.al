// Issue #3216 — a PRECOMPILED tableextension's declared keys must reach the extended table.
//
// RUNNER-MECHANISM claim, and this is the one structural reason a runner-extras bundle is the
// right home for it rather than the upstream corpus.
//
// That the keys a tableextension declares are part of the extended table's key list is plain BC
// behaviour, it is pinned upstream — codeunit 60331,
// TableExt_Key_ExtensionKeys_AreListedAmongTheExtendedTablesKeys — and that upstream test is
// what found the gap. But every corpus bundle is SOURCE-COMPILED by the runner, so the corpus
// exercises exactly one of the two readers #3216 fixed: TryParseTableExtensionFile, the
// AL-source parser. The other reader, BcAppSymbolCache's parse of a dependency package's
// SymbolReference.json, is reachable only through a precompiled dependency artifact. Rename the
// JSON property it looks for, or drop the `ext.Keys` argument at the MergeExtensionFields call
// site in RecordPatches.BcAppFallback.cs, and the precompiled path silently answers zero keys
// while codeunit 60331 and every other corpus test stays green. That is what this bundle stops.
//
// The dependency is hermetic: .alpackages/AL_Runner_Fixtures_RXK_Precompiled_Key_Dep_1.0.0.0.app
// is a hand-built NAVX symbol package (see REGENERATE-FIXTURES.txt), so no Base Application and
// no provisioned Microsoft artifact is involved and the numbers below are the same on every BC
// leg. Its table 65750 "RXK Key Base Table" declares two keys of its own; its tableextension
// 65770 declares three, one of which cannot resolve.
//
// WHAT EACH ASSERTION RULES OUT
//   KeyCount = 4        both extension keys arrived AND the unresolvable one did not. A
//                       precompiled read returning nothing answers 2; a read that registered
//                       the unresolvable key truncated to its one resolvable field answers 5.
//                       The truncated case has no other signal at all — AppendExtensionKeys
//                       reports it to Console.Error, which is not a failure mechanism here
//                       (the emit-exclusion defect dropped AL objects with exit code 0).
//   key 3 composition   the extension-only key, by field NAME, so a key registered against the
//                       wrong field id fails rather than passing on the count alone.
//   key 4 composition   a MIXED key: a base-table field first, then an extension field, in
//                       declared order. Field order IS sort order. It also proves the late
//                       name-to-id resolution in BuildNCLMetaTable, which is the only point
//                       that has both parses in hand.
//   the base keys       unchanged and still FIRST, so the merge appends rather than reorders.
codeunit 65701 "RXK Precompiled Key Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit "RXK Assert";

    // The two RESOLVABLE keys the precompiled tableextension declares reach the extended
    // table's key list, and the one that cannot resolve does not. Counted by COMPOSITION over
    // the whole list rather than asserted as a total, because BC itself contributes a SystemId
    // key of its own on top of the four the runner builds, and I could not verify on this
    // machine that its presence and position are identical on the 27.x legs (a self-built
    // runner is compiled against Ncl 28.x and cannot run 27.x artifacts). Counting is not the
    // weaker claim: a truncated ExtBroken would register as a SECOND key composed of
    // [Ext Rank], so "exactly one" fails on it where a total of 5 would not.
    [Test]
    procedure PrecompiledTableExtensionKeysAreListedAmongTheExtendedTablesKeys()
    var
        RecRef: RecordRef;
    begin
        RecRef.Open(65750);

        Assert.AreEqual(1, KeysComposed(RecRef, 'Ext Rank'),
            'exactly one key on the extended table is composed of the extension field Ext Rank');
        Assert.AreEqual(1, KeysComposed(RecRef, 'Name,Ext Tag'),
            'exactly one key on the extended table is composed of Name then Ext Tag');
        Assert.AreEqual(2, KeysCarryingAnExtensionField(RecRef),
            'exactly two of the extended table''s keys carry a field the tableextension added — the third key it declares names a field nothing declares and must be dropped WHOLE');

        RecRef.Close();
    end;

    // The base table's own keys stay first and keep their composition, so the extension keys
    // are APPENDED rather than replacing or reordering anything.
    [Test]
    procedure TheBaseTablesOwnKeysAreUnchangedAndStillFirst()
    var
        RecRef: RecordRef;
        KeyRef: KeyRef;
    begin
        RecRef.Open(65750);

        KeyRef := RecRef.KeyIndex(1);
        Assert.AreEqual(1, KeyRef.FieldCount(), 'the primary key is one field wide');
        Assert.AreEqual('ID', KeyRef.FieldIndex(1).Name(), 'the primary key is on ID');

        KeyRef := RecRef.KeyIndex(2);
        Assert.AreEqual(1, KeyRef.FieldCount(), 'the base secondary key is one field wide');
        Assert.AreEqual('Name', KeyRef.FieldIndex(1).Name(), 'the base secondary key is on Name');

        RecRef.Close();
    end;

    // Key 3 is the extension-only key. Asserting the FIELD NAME, not just a count, is what
    // makes this fail for a key registered against the wrong field.
    [Test]
    procedure TheExtensionOnlyKeyCarriesItsExtensionField()
    var
        RecRef: RecordRef;
        KeyRef: KeyRef;
    begin
        RecRef.Open(65750);

        KeyRef := RecRef.KeyIndex(3);
        Assert.AreEqual(1, KeyRef.FieldCount(), 'ExtRank is one field wide');
        Assert.AreEqual('Ext Rank', KeyRef.FieldIndex(1).Name(), 'ExtRank is on the extension field Ext Rank');

        RecRef.Close();
    end;

    // Key 4 mixes a BASE-table field with an EXTENSION field, base field first. This is the
    // arm that would fail if names were resolved to ids at parse time, when only one of the two
    // parses is in hand — and the field ORDER is the key's sort order, so it is asserted too.
    [Test]
    procedure TheMixedKeyKeepsBothHalvesInDeclaredOrder()
    var
        RecRef: RecordRef;
        KeyRef: KeyRef;
    begin
        RecRef.Open(65750);

        KeyRef := RecRef.KeyIndex(4);
        Assert.AreEqual(2, KeyRef.FieldCount(), 'ExtMixed is two fields wide');
        Assert.AreEqual('Name', KeyRef.FieldIndex(1).Name(), 'ExtMixed sorts on the BASE table field first');
        Assert.AreEqual('Ext Tag', KeyRef.FieldIndex(2).Name(), 'ExtMixed sorts on the extension field second');

        RecRef.Close();
    end;

    // NEGATIVE, the other half of the drop-it-whole rule: no key anywhere in the list names
    // 'No Such Field', and none is the two-field ExtBroken shortened to its one resolvable
    // position. AppendExtensionKeys reports that case to Console.Error and carries on, and
    // Console.Error is not a failure mechanism here — the emit-exclusion defect dropped AL
    // objects with exit code 0 — so the assertion has to be on the key list itself.
    [Test]
    procedure TheKeyNamingAnUnresolvableFieldIsNotRegisteredAtAll()
    var
        RecRef: RecordRef;
    begin
        RecRef.Open(65750);

        Assert.AreEqual(0, KeysComposed(RecRef, 'Ext Rank,No Such Field'),
            'the key naming a field nothing declares must not be registered');
        Assert.AreEqual(0, KeysComposed(RecRef, 'No Such Field'),
            'nor may it be registered with the unresolvable position quietly dropped');
        Assert.AreEqual(1, KeysComposed(RecRef, 'Ext Rank'),
            'and it must not be registered TRUNCATED to its one resolvable field, which would make a second [Ext Rank] key');

        RecRef.Close();
    end;

    // The extension FIELDS still arrive too. Without this, a regression that dropped the whole
    // extension merge would look like a key-only problem.
    [Test]
    procedure TheExtensionFieldsThemselvesStillArrive()
    var
        RecRef: RecordRef;
        FieldRef: FieldRef;
    begin
        RecRef.Open(65750);

        FieldRef := RecRef.Field(65770);
        Assert.AreEqual('Ext Rank', FieldRef.Name(), 'field 65770 comes from the precompiled tableextension');
        FieldRef := RecRef.Field(65771);
        Assert.AreEqual('Ext Tag', FieldRef.Name(), 'field 65771 comes from the precompiled tableextension');

        RecRef.Close();
    end;

    // The number of keys whose field-name composition is exactly Composition (comma-joined,
    // in key order). Counting rather than indexing is what makes a duplicate visible.
    local procedure KeysComposed(var RecRef: RecordRef; Composition: Text): Integer
    var
        KeyRef: KeyRef;
        i: Integer;
        j: Integer;
        Actual: Text;
        Hits: Integer;
    begin
        for i := 1 to RecRef.KeyCount() do begin
            KeyRef := RecRef.KeyIndex(i);
            Actual := '';
            for j := 1 to KeyRef.FieldCount() do begin
                if Actual <> '' then
                    Actual := Actual + ',';
                Actual := Actual + KeyRef.FieldIndex(j).Name();
            end;
            if Actual = Composition then
                Hits := Hits + 1;
        end;
        exit(Hits);
    end;

    // How many of the table's keys mention a field the tableextension added. Two is the whole
    // claim: both resolvable extension keys arrived, and nothing else did.
    local procedure KeysCarryingAnExtensionField(var RecRef: RecordRef): Integer
    var
        KeyRef: KeyRef;
        i: Integer;
        j: Integer;
        FieldName: Text;
        Hits: Integer;
    begin
        for i := 1 to RecRef.KeyCount() do begin
            KeyRef := RecRef.KeyIndex(i);
            for j := 1 to KeyRef.FieldCount() do begin
                FieldName := KeyRef.FieldIndex(j).Name();
                if (FieldName = 'Ext Rank') or (FieldName = 'Ext Tag') then begin
                    Hits := Hits + 1;
                    break;
                end;
            end;
        end;
        exit(Hits);
    end;

}
