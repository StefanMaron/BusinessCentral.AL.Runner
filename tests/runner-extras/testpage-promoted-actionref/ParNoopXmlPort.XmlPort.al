/// An xmlport that exists solely to be the TARGET of an action's `RunObject = xmlport ...`.
/// Sibling of "Par Noop Report" and "Par Noop Runner"; see the codeunit for why all four kinds
/// now have a target rather than only the report.
///
/// Direction = Export with no request page, so nothing here waits on a stream a test session
/// cannot supply — the fixture must be refusable for being an XMLPORT, not for being an
/// xmlport that could not have run anyway.
///
/// As with the codeunit, OnPreXmlPort writes a log row so that a regression which actually RAN
/// the xmlport is caught, instead of being indistinguishable from the refusal.
xmlport 64550 "Par Noop XmlPort"
{
    Direction = Export;
    Format = Xml;
    UseRequestPage = false;

    schema
    {
        textelement(RootNode)
        {
            tableelement(Row; "Par Row")
            {
                fieldattribute(No; Row."No.") { }
            }
        }
    }

    trigger OnPreXmlPort()
    var
        OpenLog: Record "Par Open Log";
    begin
        OpenLog.Log('XMLPORT-RAN');
    end;
}
