// The worker an install trigger starts. A Normal codeunit, source-compiled in this bundle:
// the claim here is about the GUARD, not about resolving the async OnRunAsync flavour BC's
// compiler emits for precompiled codeunits — that is issue #2826's suite, which needs a real
// Base Application worker and its own isolation-disabled invocation. Keeping this one
// source-compiled means this bundle needs no Base App and costs nothing extra.
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
