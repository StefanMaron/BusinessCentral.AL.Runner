// Deliberately empty of behaviour. This app is a fixture: what the test next door reads is its
// MANIFEST identity, reflected into NAV App Installed App (2000000153) by the runner's seeder.
// An object has to exist for the app to compile and be loaded at all.
codeunit 65770 "NIAD Dummy"
{
    procedure Ping(): Integer
    begin
        exit(973);
    end;
}
