/// An XmlPort that will fail Roslyn compilation because NavXmlPort types
/// are not available in standalone mode. Its exclusion from the Roslyn
/// assembly is surfaced in the JSON output as a "compilationErrors" entry,
/// while the Order Calculator codeunit continues to compile and run.
xmlport 50841 "Order Export"
{
    Direction = Export;
    Format = Xml;

    schema
    {
        textelement(Orders)
        {
            tableelement(OrderLine; "Order Line")
            {
                fieldelement(EntryNo; OrderLine."Entry No.") { }
                fieldelement(OrderNo; OrderLine."Order No.") { }
                fieldelement(Amount; OrderLine.Amount) { }
            }
        }
    }
}
