// Codeunit that uses #if/#endif preprocessor guards.
// The runner must handle these directives gracefully: undefined symbols
// cause their guarded blocks to be excluded, and the codeunit still compiles.
codeunit 1900007 "Preproc Helper"
{
    // This block is always included (no guard).
    procedure AlwaysPresent(): Integer
    begin
        exit(10);
    end;

    // This block is excluded because UNDEFINED_SYMBOL is never defined.
    // If the block were included, it would still be valid AL — the test
    // verifies the runner does not crash on #if/#endif directives.
#if UNDEFINED_SYMBOL
    procedure OnlyWhenSymbolDefined(): Integer
    begin
        exit(99);
    end;
#endif
}
