// The worker the install trigger asks StartSession to run. The install pass refuses the call
// (#3292), so on a correct runner this OnRun never executes from there; its FROM-INSTALL row is
// the evidence if it does. Source-compiled, so this bundle needs no Base App.
codeunit 60717 "ITS Session Worker"
{
    trigger OnRun()
    var
        Marker: Record "ITS Session Marker";
    begin
        Marker.Init();
        Marker."Code" := 'FROM-INSTALL';
        Marker."Value" := 42;
        Marker.Insert();
    end;
}
