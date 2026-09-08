// Declares its Subtype as a QUOTED identifier — legal AL, and the same declaration as
// `Subtype = Upgrade;`. #3536: the runner's source-decl parser used to carry the quote
// characters through to the column resolver, which then matched no member and refused.
codeunit 60765 "CMV Quoted"
{
    Subtype = "Upgrade";

    trigger OnUpgradePerCompany()
    begin
    end;
}
