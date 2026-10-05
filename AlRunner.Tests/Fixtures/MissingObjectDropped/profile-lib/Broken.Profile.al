/// <summary>#5339: a profile naming a role center that does not exist, so the compile drops it. A profile declares no
/// executable AL, so the runner lets the module run and its drop must not stop a missing id reading as absent.</summary>
profile "MOD Dropped Profile"
{
    RoleCenter = "MOD Nonexistent Role Center";
}
