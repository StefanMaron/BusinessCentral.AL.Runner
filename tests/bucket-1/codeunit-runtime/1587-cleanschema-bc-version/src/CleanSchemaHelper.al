// Helper for issue #1587: CLEANSCHEMA symbols must derive from BC version.
// Tests that the default CLEANSCHEMA set (1..25) is applied correctly:
// CLEANSCHEMA1 is active, so #if not CLEANSCHEMA1 blocks are excluded,
// and #if CLEANSCHEMA1 blocks are included.
codeunit 1587100 "CS Version Helper"
{
    // Always present (no guard). Base case.
    procedure AlwaysPresent(): Integer
    begin
        exit(100);
    end;

    // CLEANSCHEMA1 is in the default set — this block IS included.
    // Verifies that a positive CLEANSCHEMA guard works at runtime.
#if CLEANSCHEMA1
    procedure WhenCS1Active(): Integer
    begin
        exit(1);
    end;
#endif

    // CLEANSCHEMA25 is in the default set — this block IS included.
    // Verifies the upper end of the default set (max=25 when no app version).
#if CLEANSCHEMA25
    procedure WhenCS25Active(): Integer
    begin
        exit(25);
    end;
#endif
}
