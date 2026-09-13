#if not RUNNER_EXTRAS_MPS
codeunit 65962 "MPS Only Undefined"
{
    procedure Touch(): Integer
    begin
        exit(65962);
    end;
}
#endif
