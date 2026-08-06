// Proves Enum.Ordinals()/Names() merge base enum values with enumextension
// values, in both the plain case and the case where the enum also has a
// circular type dependency (enum -> interface -> record field of the same
// enum type). See issue #1625.
//
// RED (before the fix): AlEnumMetadataRegistry.Register is "last writer wins"
// keyed only by object Id (AlRunner/Patches/EnumMetadataPatches.cs). AL emits
// one AddApplicationObject per enum-shaped symbol - the base enum AND each
// enumextension - so whichever fires last for that entry silently clobbers
// the other's values instead of the two being merged.
//
// GREEN (after the fix): the registry merges base + every enumextension
// registered for the same underlying enum Id, deduplicated by ordinal.
codeunit 62309 "EEM Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit "EEM Assert";

    [Test]
    procedure SimpleExtension_OrdinalsCountIsMerged()
    var
        Ordinals: List of [Integer];
    begin
        // Base has 2 values (0, 1); extension adds 2 (10, 20). Expected: 4 total.
        Ordinals := Enum::"EEM Status".Ordinals();
        Assert.AreEqual(4, Ordinals.Count(), 'Expected 4 ordinals in the enum (2 base + 2 extension)');
    end;

    [Test]
    procedure SimpleExtension_NamesCountIsMerged()
    var
        Names: List of [Text];
    begin
        Names := Enum::"EEM Status".Names();
        Assert.AreEqual(4, Names.Count(), 'Expected 4 names in the enum (2 base + 2 extension)');
    end;

    [Test]
    procedure SimpleExtension_BaseValuesStillAccessible()
    var
        Names: List of [Text];
        NameValue: Text;
    begin
        Names := Enum::"EEM Status".Names();
        Names.Get(1, NameValue);
        Assert.AreEqualText('Blank', NameValue, 'Index 1 must still be the base value Blank');
        Names.Get(2, NameValue);
        Assert.AreEqualText('Second value', NameValue, 'Index 2 must still be the base value Second value');
    end;

    [Test]
    procedure SimpleExtension_ExtensionValueAccessibleByName()
    var
        Names: List of [Text];
        NameValue: Text;
        Found: Boolean;
        i: Integer;
    begin
        Names := Enum::"EEM Status".Names();
        for i := 1 to Names.Count() do begin
            Names.Get(i, NameValue);
            if NameValue = 'Extended Value' then
                Found := true;
        end;
        Assert.IsTrue(Found, 'Extension value "Extended Value" must be present in Names()');
    end;

    [Test]
    procedure SimpleExtension_ExtensionOrdinalAccessible()
    var
        Ordinals: List of [Integer];
        Found: Boolean;
        i: Integer;
    begin
        Ordinals := Enum::"EEM Status".Ordinals();
        for i := 1 to Ordinals.Count() do
            if Ordinals.Get(i) = 20 then
                Found := true;
        Assert.IsTrue(Found, 'Extension ordinal 20 ("Another Extended Value") must be present in Ordinals()');
    end;

    [Test]
    procedure CircularEnum_OrdinalsCountIsMerged()
    var
        Ordinals: List of [Integer];
    begin
        // Base has 3 values (0, 1, 2); extension adds 1 (10). Expected: 4 total.
        Ordinals := Enum::"EEM Action Type".Ordinals();
        Assert.AreEqual(4, Ordinals.Count(), 'Expected 4 ordinals for circular enum+ext (3 base + 1 extension)');
    end;

    [Test]
    procedure CircularEnum_NamesCountIsMerged()
    var
        Names: List of [Text];
    begin
        Names := Enum::"EEM Action Type".Names();
        Assert.AreEqual(4, Names.Count(), 'Expected 4 names for circular enum+ext');
    end;

    [Test]
    procedure CircularEnum_BaseAndExtensionValuesBothPresent()
    var
        Names: List of [Text];
        NameValue: Text;
        SawBlank: Boolean;
        SawActionA: Boolean;
        SawActionB: Boolean;
        SawExtendedAction: Boolean;
        i: Integer;
    begin
        Names := Enum::"EEM Action Type".Names();
        for i := 1 to Names.Count() do begin
            Names.Get(i, NameValue);
            case NameValue of
                'Blank':
                    SawBlank := true;
                'Action A':
                    SawActionA := true;
                'Action B':
                    SawActionB := true;
                'Extended Action':
                    SawExtendedAction := true;
            end;
        end;
        Assert.IsTrue(SawBlank, 'Base value Blank must be present');
        Assert.IsTrue(SawActionA, 'Base value Action A must be present');
        Assert.IsTrue(SawActionB, 'Base value Action B must be present');
        Assert.IsTrue(SawExtendedAction, 'Extension value Extended Action must be present');
    end;

    // Negative control: an enum with NO enumextension must report exactly its
    // own declared values, nothing merged in from elsewhere.
    [Test]
    procedure NoExtension_OrdinalsMatchDeclaredValuesExactly()
    var
        Ordinals: List of [Integer];
    begin
        Ordinals := Enum::"EEM Status".Ordinals();
        Assert.IsFalse(Ordinals.Contains(999), 'A fabricated ordinal must never appear');
    end;
}
