/// <summary>
/// The control report's source table. Deliberately a plain table rather than the `Integer`
/// virtual table: `Integer` cannot be resolved without declaring the Base Application floor
/// on this app, and the claim under test is about a data item's declared BOUNDS, not about
/// which table supplies its rows. The dependency's report 65821 keeps `Integer`, because
/// there the window size is what makes the missing bound observable.
/// </summary>
table 65844 "RSM Row"
{
    DataClassification = ToBeClassified;
    fields
    {
        field(1; "No."; Integer) { }
    }
    keys { key(PK; "No.") { Clustered = true; } }
}
