// EnumDefaultImplementationTests — #2306.
//
// An AL enum can name its implementing codeunit in three places:
//
//     enum 205 "Alt. Cust VAT Reg. Doc." implements "Alt. Cust. VAT Reg. Doc."
//     {
//         DefaultImplementation = "Alt. Cust. VAT Reg. Doc." = "Alt. Cust. VAT Reg. Doc. Impl.";
//         UnknownImplementation = ...;
//         value(0; Default) { Implementation = ... }   // per value
//     }
//
// AlEnumOptionMetadata only carried the per-value one, so an enum that names only a
// DefaultImplementation — Base App 205 is exactly that, one value with no Implementation of
// its own — resolved to -1 and `exit("Alt. Cust VAT Reg. Doc."::Default)` threw. It reached
// 30 tests in Microsoft's Tests-SINGLESERVER bucket, through
// Codeunit 207.GetAltCustVATRegDocImpl on every Sales Header insert.
//
// BC's NCLEnumMetadata.GetImplementationCodeunitId, decompiled from Ncl 28.1, is a three-step
// fallback and these tests pin each step:
//
//     per-value implementations[interfaceIndex]                       if the ordinal has any
//     else defaultImplementations[interfaceIndex]                     if the ordinal is known
//     else unknownImplementations[interfaceIndex]                     if it is not
//
// with a value of 0 or less at either fallback meaning "no implementer" (-1), not "codeunit 0".
using AlRunner;
using Xunit;

namespace AlRunner.Tests;

public sealed class EnumDefaultImplementationTests
{
    // The shape of Base App enum 205: one value, no per-value Implementation, one
    // DefaultImplementation.
    private static AlEnumOptionMetadata OnlyADefaultImplementation()
        => new(
            name: "Alt. Cust VAT Reg. Doc.", id: 205,
            options: new[] { "Default" }, indexes: new[] { 0 },
            implementations: null, captions: null,
            defaultImplementations: new[] { 6207 }, unknownImplementations: null);

    [Fact]
    public void KnownValueWithNoImplementationOfItsOwn_FallsBackToTheDefault()
        => Assert.Equal(6207, OnlyADefaultImplementation().GetImplementationCodeunitIdPublic(0, 0));

    [Fact]
    public void PerValueImplementation_WinsOverTheDefault()
    {
        var meta = new AlEnumOptionMetadata(
            name: "Two Ways", id: 2306001,
            options: new[] { "A", "B" }, indexes: new[] { 0, 1 },
            implementations: new[] { new[] { 111 }, System.Array.Empty<int>() },
            captions: null,
            defaultImplementations: new[] { 999 }, unknownImplementations: null);

        Assert.Equal(111, meta.GetImplementationCodeunitIdPublic(0, 0));
        // B declares none, so it takes the default — the two must not be confused.
        Assert.Equal(999, meta.GetImplementationCodeunitIdPublic(1, 0));
    }

    [Fact]
    public void UnknownOrdinal_UsesTheUnknownImplementation_NotTheDefault()
    {
        var meta = new AlEnumOptionMetadata(
            name: "Extensible", id: 2306002,
            options: new[] { "A" }, indexes: new[] { 0 },
            implementations: null, captions: null,
            defaultImplementations: new[] { 111 }, unknownImplementations: new[] { 222 });

        Assert.Equal(111, meta.GetImplementationCodeunitIdPublic(0, 0));
        Assert.Equal(222, meta.GetImplementationCodeunitIdPublic(7, 0));
    }

    [Fact]
    public void AnInterfaceIndexPastTheDeclaredList_HasNoImplementer()
        // An enum implementing one interface has one entry; asking for a second is -1, not
        // an out-of-range read.
        => Assert.Equal(-1, OnlyADefaultImplementation().GetImplementationCodeunitIdPublic(0, 1));

    [Fact]
    public void ADefaultOfZeroOrLess_MeansNoImplementer()
    {
        // BC reads `if (num <= 0) return -1;` — an explicit 0 is "none declared", never
        // codeunit 0, and returning 0 would build a NavCodeunitHandle for a nonexistent
        // object instead of failing.
        var meta = new AlEnumOptionMetadata(
            name: "Zeroed", id: 2306003,
            options: new[] { "A" }, indexes: new[] { 0 },
            implementations: null, captions: null,
            defaultImplementations: new[] { 0 }, unknownImplementations: null);

        Assert.Equal(-1, meta.GetImplementationCodeunitIdPublic(0, 0));
    }

    [Fact]
    public void NoImplementationsAtAll_StillResolvesToMinusOne()
        => Assert.Equal(-1, new AlEnumOptionMetadata(
                name: "Bare", id: 2306004,
                options: new[] { "A" }, indexes: new[] { 0 })
            .GetImplementationCodeunitIdPublic(0, 0));
}
