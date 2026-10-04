// #5309: a library that opens a page by id. It does not depend on the bundle that declares the page, so the call
// is the only thing that connects a test to that page's triggers.
codeunit 72850 "TPI Opener"
{
    procedure OpenById(PageId: Integer)
    begin
        Page.Run(PageId);
    end;
}
