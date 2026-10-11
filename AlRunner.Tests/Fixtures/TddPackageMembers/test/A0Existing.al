// Extensions the test app already declares: --tdd's own must not take their ids, nor their field id or ordinal.
tableextension 65400 "Local Entry Ext" extends "Loyalty Entry"
{
    fields
    {
        field(65401; "Local Points"; Integer) { }
    }
}

enumextension 65400 "Local Tier Ext" extends "Loyalty Tier"
{
    value(65401; Local) { }
}
