// Fixture. The bundle tables carry the names of Base Application's "Shipment Method" (10) and
// "Shipping Agent" (291), under a namespace the writing tables import. The shipping agent's
// primary key is (Code, Seq) so one Code can carry two rows here and one in Base Application.
namespace ALR.RelationTwin.Imported;

table 66600 "Shipment Method"
{
    DataClassification = SystemMetadata;

    fields
    {
        field(1; Code; Code[10]) { }
    }

    keys
    {
        key(PK; Code) { Clustered = true; }
    }
}

table 66601 "Shipping Agent"
{
    DataClassification = SystemMetadata;

    fields
    {
        field(1; Code; Code[10]) { }
        field(2; Seq; Integer) { }
    }

    keys
    {
        key(PK; Code, Seq) { Clustered = true; }
    }
}
