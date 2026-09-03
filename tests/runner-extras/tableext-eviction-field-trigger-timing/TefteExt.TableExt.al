/// Merely EXISTING triggers the eviction under test (RecordPatches.MergeExtensionFields ->
/// EvictCachedMetaTableForBaseTable) once this app's AL source is parsed -- "Tefte T" was
/// already built+wired by the sibling "dep" app's own install (see dep/TefteInstall.Codeunit.al).
/// The new field itself is never read by the test; only the pre-existing field 2 OnValidate
/// trigger's survival across the eviction is under test. See app.json for the full writeup.
tableextension 65276 "Tefte Ext" extends "Tefte T"
{
    fields
    {
        field(4; "Extra"; Code[10]) { }
    }
}
