enum 50510 "IV Mode"
{
    value(0; " ") { }
    value(1; Daily) { }
    value(2; Weekly) { }
    value(3; Off) { }
}

table 50510 "IV Settings"
{
    fields
    {
        field(1; Id; Integer) { }
        field(2; Mode; Enum "IV Mode")
        {
            InitValue = Daily;
        }
        field(3; "Retention Days"; Integer)
        {
            InitValue = 30;
        }
        field(4; "Enabled"; Boolean)
        {
            InitValue = true;
        }
    }
    keys { key(PK; Id) { Clustered = true; } }
}
