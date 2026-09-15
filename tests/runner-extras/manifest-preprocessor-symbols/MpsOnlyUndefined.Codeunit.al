#if not RUNNER_EXTRAS_MPS
codeunit 65972 "MPS Only Undefined"
{
    procedure Touch(): Integer
    begin
        exit(65972);
    end;
}
#endif
