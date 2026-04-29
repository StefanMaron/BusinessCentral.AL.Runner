// Deliberately malformed AL: the procedure signature is missing its return-type
// closing parenthesis and the codeunit body is missing the closing brace, which
// guarantees the transpiler can't produce a usable assembly. Used by the
// protocol-v2 server tests to verify the compilation-failure summary path.
codeunit 50300 Broken
{
    procedure Compute(n: Integer: Integer
    begin
        exit(n * 2);
    end;
