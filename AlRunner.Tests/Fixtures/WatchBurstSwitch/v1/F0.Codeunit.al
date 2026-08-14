/// <summary>Version A addend #0: always returns 1. WatchBurstSwitchTests overwrites
/// this file's content to the "version B" value (10) as part of the burst switch.</summary>
codeunit 60201 "Burst F0 RXT"
{
    procedure GetValue(): Integer
    begin
        exit(1);
    end;
}
