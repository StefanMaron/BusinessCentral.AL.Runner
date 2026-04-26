/// Helper codeunit for System built-ins:
/// CompressArray, CopyArray, CopyStream, CreateGuid, Random, Randomize.
codeunit 97500 "SAR Src"
{
    // ── CompressArray ─────────────────────────────────────────────────────────

    procedure CompressTextArray(var Arr: array[5] of Text)
    begin
        CompressArray(Arr);
    end;

    // ── CopyArray ─────────────────────────────────────────────────────────────

    procedure CopyIntArray(var FromArr: array[5] of Integer; var ToArr: array[5] of Integer; Count: Integer)
    begin
        CopyArray(ToArr, FromArr, 1, Count);
    end;

    // ── CreateGuid ────────────────────────────────────────────────────────────

    procedure NewGuid(): Guid
    begin
        exit(CreateGuid());
    end;

    // ── Random ────────────────────────────────────────────────────────────────

    procedure Rnd(Max: Integer): Integer
    begin
        exit(Random(Max));
    end;

    // ── Randomize ────────────────────────────────────────────────────────────

    procedure SeedRnd(Seed: Integer)
    begin
        Randomize(Seed);
    end;

    procedure SeedRndNoArg()
    begin
        Randomize();
    end;
}
