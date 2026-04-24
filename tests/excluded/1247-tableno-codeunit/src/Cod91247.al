codeunit 91247 "No Test Probe"
{
    trigger OnRun()
    begin
        Message('I run via OnRun, not as a test');
    end;
}
