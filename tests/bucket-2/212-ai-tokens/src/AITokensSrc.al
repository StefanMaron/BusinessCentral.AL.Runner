/// Proves SessionInformation.AITokensUsed — rewritten to 0L standalone.
codeunit 60390 "AIT Src"
{
    procedure GetAITokens(): BigInteger
    begin
        exit(SessionInformation.AITokensUsed());
    end;
}
