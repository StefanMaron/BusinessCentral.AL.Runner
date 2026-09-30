/// <summary>
/// #5044: Platinum is a value of "Loyalty Tier" added by an enumextension, so an argument
/// naming it must still generate an Enum "Loyalty Tier" parameter.
/// </summary>
enumextension 65104 "Loyalty Tier Ext" extends "Loyalty Tier"
{
    value(10; Platinum) { }
}
