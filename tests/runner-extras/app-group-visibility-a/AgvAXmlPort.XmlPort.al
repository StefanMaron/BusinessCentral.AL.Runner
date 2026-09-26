// #4461: an xmlport in this app group, so XMLport Metadata (2000000280) has a subject in every
// group. Without one, every "group X does not see group Y's xmlport" assertion is vacuous.
xmlport 62606 "AGV A XmlPort"
{
    Caption = 'AGV A XmlPort Caption';
    Direction = Export;

    schema
    {
        textelement(AgvRoot)
        {
            tableelement(AgvRow; "AGV A Table")
            {
                fieldelement(CodeCol; AgvRow."Code") { }
            }
        }
    }
}
