// Fixture. Written in the namespace of Base Application's "Shipment Method" and "Shipping
// Agent", importing the namespace of the same-named bundle tables. "ALR Twin Mod" is the
// same table as "ALR Twin Plain" with a modify(...) tableextension on it.
namespace Microsoft.Foundation.Shipping;

using ALR.RelationTwin.Imported;

table 66602 "ALR Twin Plain"
{
    DataClassification = SystemMetadata;

    fields
    {
        field(1; Code; Code[10]) { }
        field(2; "Method Code"; Code[10])
        {
            TableRelation = "Shipment Method";
        }
        field(3; "Agent Rows"; Integer)
        {
            FieldClass = FlowField;
            CalcFormula = count("Shipping Agent" where(Code = field(Code)));
        }
    }

    keys
    {
        key(PK; Code) { Clustered = true; }
    }
}

table 66603 "ALR Twin Mod"
{
    DataClassification = SystemMetadata;

    fields
    {
        field(1; Code; Code[10]) { }
        field(2; "Method Code"; Code[10])
        {
            TableRelation = "Shipment Method";
        }
        field(3; "Agent Rows"; Integer)
        {
            FieldClass = FlowField;
            CalcFormula = count("Shipping Agent" where(Code = field(Code)));
        }
    }

    keys
    {
        key(PK; Code) { Clustered = true; }
    }
}

tableextension 66604 "ALR Twin Mod Ext" extends "ALR Twin Mod"
{
    fields
    {
        modify("Method Code")
        {
            Caption = 'Method Code (modified)';
        }
    }
}
