// Parent + child tables for the in-memory query-join reproducer.
//
// QJ Order.Customer No. is the foreign key linking each order to its QJ Customer.
// The query "QJ Cust Orders" joins them so each result row pairs a customer Name
// with one of that customer's order Amounts.

table 60300 "QJ Customer"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "No."; Code[20]) { }
        field(2; "Name"; Text[50]) { }
    }

    keys
    {
        key(PK; "No.") { Clustered = true; }
    }
}

table 60301 "QJ Order"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "Entry No."; Integer) { }
        field(2; "Customer No."; Code[20]) { }
        field(3; "Amount"; Decimal) { }
    }

    keys
    {
        key(PK; "Entry No.") { Clustered = true; }
    }
}
