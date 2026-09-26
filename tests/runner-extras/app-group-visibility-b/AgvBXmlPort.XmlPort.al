// #4461: an xmlport in this app group, so XMLport Metadata (2000000280) has a subject in every
// group. Without one, every "group X does not see group Y's xmlport" assertion is vacuous.
xmlport 62616 "AGV B XmlPort"
{
    Caption = 'AGV B XmlPort Caption';
    Direction = Export;

    schema
    {
        textelement(AgvRoot)
        {
            tableelement(AgvRow; "AGV B Table")
            {
                fieldelement(CodeCol; AgvRow."Code") { }
            }
        }
    }
}
