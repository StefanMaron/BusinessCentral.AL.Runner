/// Merely EXISTING triggers the eviction under test (RecordPatches.MergeExtensionFields ->
/// EvictCachedMetaTableForBaseTable) once this app's AL source is parsed -- "Sett T" was
/// already built+wired (subscribers injected) by the sibling "dep" app's own install (see
/// dep/SettInstall.Codeunit.al). The new field itself is never read by the test; only the
/// pre-existing subscribers' survival across the eviction is under test. See app.json for the
/// full writeup.
tableextension 65526 "Sett Ext" extends "Sett T"
{
    fields
    {
        field(5; "Extra"; Code[10]) { }
    }
}
