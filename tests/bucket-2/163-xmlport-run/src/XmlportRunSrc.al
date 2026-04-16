/// Minimal xmlport object — exercised for Xmlport.Run dispatch.
xmlport 59760 "XPR Minimal"
{
    Direction = Export;
    Format = Xml;

    schema
    {
        textelement(root)
        {
            textattribute(version) { }
        }
    }
}

/// Helper codeunit exercising Xmlport.Run.
codeunit 59760 "XPR Src"
{
    procedure CallRun()
    begin
        Xmlport.Run(Xmlport::"XPR Minimal");
    end;

    procedure CallRunAndReturnFlag(): Boolean
    begin
        Xmlport.Run(Xmlport::"XPR Minimal");
        exit(true);
    end;

    procedure CallRunWithShowPage(showPage: Boolean; showXml: Boolean): Boolean
    begin
        Xmlport.Run(Xmlport::"XPR Minimal", showPage, showXml);
        exit(true);
    end;
}
