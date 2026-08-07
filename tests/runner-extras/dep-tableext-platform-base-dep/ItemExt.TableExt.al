/// <summary>
/// TableExtension defined in the DEPENDENCY app on the PLATFORM table Item (27).
/// Item's own base-table metadata is served from the precompiled Base Application
/// .app — this bundle has no AL source for Item itself. Two fields on purpose: a
/// Boolean and an Integer, so a stub implementation that only handles one AL type
/// can't accidentally pass every test.
/// </summary>
tableextension 61881 "DTB Item Ext" extends Item
{
    fields
    {
        field(61881; "DTB Repro Flag"; Boolean) { DataClassification = CustomerContent; }
        field(61882; "DTB Repro Counter"; Integer) { DataClassification = CustomerContent; }
    }
}
