// DataClassificationParseRefusalTests — issue #3602.
//
// BuildMetaField used to hand a DataClassification to MetaField's constructor only when
// Enum.TryParse succeeded, and did nothing at all when it failed. Doing nothing leaves the
// argument at its constructor default, which for ALDataClassification is CustomerContent — so a
// value the runner could not understand became a real, plausible classification with nothing in
// the built metadata to say it was invented. That is the silent fallback
// .claude/rules/loud-failures.md forbids: the failure mode is a green run carrying a wrong
// value, not a red one.
//
// LATENT, NOT LIVE. Every DataClassificationName reaching this code today comes from BC's own
// symbol file, so the parse does not fail in any measured population — AllDataClassifications
// below is enumerated off the shipped ALDataClassification enum and every member of it parses.
// The trap is the NEXT BC version adding a member the runner has not seen, or a caller reaching
// here with a value from somewhere other than a symbol file.
//
// WHY THIS IS NOT A BC-BEHAVIOUR TEST, AND NOT A CORPUS TEST
// ---------------------------------------------------------
// The claim is not "BC classifies field X as Y" — that would belong upstream. The claim is that
// the RUNNER'S OWN metadata builder refuses a value it cannot map instead of substituting one.
// A corpus test structurally cannot express it: an unparseable DataClassification cannot come
// out of a symbol file, because the AL compiler rejects the source before a symbol file exists,
// so no AL a service tier will accept can reach the refused branch.
//
// WHY THE BUILDER IS CALLED BY REFLECTION
// ---------------------------------------
// BuildMetaField is private and its public entry point is the full NCLMetaTable build, which
// needs the whole engine standing up. The reflection statics it reads (_tMetaField,
// _tALDataClassification, _tNavType, _tFieldClass) are otherwise assigned only by
// RecordPatches.Register(); they are set here from the SAME Microsoft.Dynamics.Nav.Types types
// Register() resolves, which this test project already references directly — idempotent with
// Register() rather than a substitute for it. Same technique, and the same
// save-and-restore discipline, as ObjectRefConstCallSiteWiringTests.
using System.Reflection;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

// MUST be serial: writes the process-global metadata reflection statics.
[Collection(RecordPatchesSerialCollection.Name)]
public sealed class DataClassificationParseRefusalTests : IDisposable
{
    private const int FieldId = 7;
    private const string FieldName = "DC Probe Field";

    private readonly Dictionary<string, object?> _savedStatics = new();

    public DataClassificationParseRefusalTests() => EnsureMetadataReflection();

    public void Dispose()
    {
        foreach (var (name, value) in _savedStatics)
            try { Static(name).SetValue(null, value); } catch { }
    }

    private static Type AlDataClassificationType =>
        typeof(Microsoft.Dynamics.Nav.Types.Metadata.MetaTable).Assembly
            .GetType("Microsoft.Dynamics.Nav.Types.Metadata.ALDataClassification")
        ?? throw new InvalidOperationException(
            "ALDataClassification not found in Microsoft.Dynamics.Nav.Types — this test tracks that type.");

    /// <summary>
    /// Every member of the SHIPPED enum, read off the assembly rather than hardcoded, so this
    /// stays the real live-path control when a BC version adds a member. The name is exactly
    /// what a symbol file's DataClassification property carries.
    /// </summary>
    public static TheoryData<string> AllDataClassifications()
    {
        var data = new TheoryData<string>();
        foreach (var n in Enum.GetNames(AlDataClassificationType)) data.Add(n);
        return data;
    }

    // ---- the refusal -------------------------------------------------------------------

    [Fact]
    public void UnparseableDataClassification_IsRefused_NamingTheValueAndTheField()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => BuildField("NotARealClassification"));

        // The message content is the deliverable, not the throw: a refusal that does not name
        // the offending value leaves the reader exactly where the silent fallback did.
        Assert.Contains("NotARealClassification", ex.Message);
        Assert.Contains(FieldName, ex.Message);
        Assert.Contains(FieldId.ToString(), ex.Message);
        Assert.Contains("DataClassification", ex.Message);
        // ...and it must say what the silent alternative WAS, or the next reader re-derives it.
        Assert.Contains("CustomerContent", ex.Message);
        Assert.Contains("#3602", ex.Message);
    }

    [Fact]
    public void RefusalNamesTheOfferedValue_NotAFixedString()
    {
        // A refusal that hardcoded one example value would pass the test above. Two different
        // bad values must produce two different messages, each naming its own.
        var a = Assert.Throws<InvalidOperationException>(() => BuildField("Zzz_First_Bad"));
        var b = Assert.Throws<InvalidOperationException>(() => BuildField("Zzz_Second_Bad"));

        Assert.Contains("Zzz_First_Bad", a.Message);
        Assert.DoesNotContain("Zzz_Second_Bad", a.Message);
        Assert.Contains("Zzz_Second_Bad", b.Message);
        Assert.DoesNotContain("Zzz_First_Bad", b.Message);
    }

    // ---- the live path, which must be untouched ----------------------------------------

    [Theory]
    [MemberData(nameof(AllDataClassifications))]
    public void EveryShippedMemberStillParses_AndReachesTheMetaFieldAsItself(string member)
    {
        var built = BuildField(member);

        var actual = ReadDataClassification(built);
        Assert.NotNull(actual);
        Assert.Equal(member, actual!.ToString());
    }

    [Theory]
    [MemberData(nameof(AllDataClassifications))]
    public void ShippedMembersParseCaseInsensitively(string member)
    {
        // AL keywords are case-insensitive and the parse has always been ignoreCase; the
        // refusal must not narrow that. Lower-cased so a member that is already all-upper
        // still exercises a spelling different from the declared one.
        var built = BuildField(member.ToLowerInvariant());

        Assert.Equal(member, ReadDataClassification(built)?.ToString());
    }

    [Fact]
    public void CustomerContent_IsCarriedBecauseItWasDeclared_NotBecauseNothingWasPassed()
    {
        // The one value the old fallback was indistinguishable from. It must still arrive, and
        // it must arrive because the field declared it.
        Assert.Equal("CustomerContent", ReadDataClassification(BuildField("CustomerContent"))?.ToString());
    }

    [Fact]
    public void NoDeclaredDataClassification_IsNotRefused()
    {
        // Absent is not unparseable. A field that declares nothing leaves the ctor default
        // standing, exactly as before — refusing here would break every field in every table
        // that says nothing, which is most of them.
        var built = BuildField(null);
        Assert.NotNull(built);

        var blank = BuildField("   ");
        Assert.NotNull(blank);
    }

    // ---- plumbing ----------------------------------------------------------------------

    private static object BuildField(string? dataClassification)
    {
        var f = new ParsedField(
            FieldId: FieldId,
            FieldName: FieldName,
            TypeName: "Code[20]",
            Length: 20,
            DataClassificationName: dataClassification);

        return Invoke("BuildMetaField", f, 0, false, null)
               ?? throw new InvalidOperationException("BuildMetaField answered null.");
    }

    /// <summary>
    /// The DataClassification the constructed MetaField actually carries. Read off the built
    /// object rather than inferred from the argument array, so the assertion is about what BC
    /// would see.
    /// </summary>
    private static object? ReadDataClassification(object metaField)
    {
        var t = metaField.GetType();
        var p = t.GetProperty("DataClassification", BindingFlags.Public | BindingFlags.Instance);
        if (p != null) return p.GetValue(metaField);

        var fld = t.GetField("dataClassification",
                      BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public)
                  ?? throw new InvalidOperationException(
                      $"{t.FullName} exposes neither a DataClassification property nor a "
                      + "dataClassification field — this test tracks that shape.");
        return fld.GetValue(metaField);
    }

    private void EnsureMetadataReflection()
    {
        var types = typeof(Microsoft.Dynamics.Nav.Types.Metadata.MetaTable).Assembly;
        foreach (var (field, typeName) in new[]
                 {
                     ("_tMetaField",             "Microsoft.Dynamics.Nav.Types.Metadata.MetaField"),
                     ("_tALDataClassification",  "Microsoft.Dynamics.Nav.Types.Metadata.ALDataClassification"),
                     ("_tNavType",               "Microsoft.Dynamics.Nav.Types.NavType"),
                     ("_tFieldClass",            "Microsoft.Dynamics.Nav.Types.Metadata.FieldClass"),
                     ("_tObsoleteState",         "Microsoft.Dynamics.Nav.Types.Metadata.ObsoleteState"),
                 })
        {
            var t = types.GetType(typeName)
                    ?? throw new InvalidOperationException($"{typeName} not found in {types.GetName().Name}.");
            var fi = Static(field);
            _savedStatics[field] = fi.GetValue(null);
            fi.SetValue(null, t);
        }
    }

    private static FieldInfo Static(string name) =>
        typeof(RecordPatches).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException($"RecordPatches.{name} not found — this test tracks that field.");

    private static object? Invoke(string method, params object?[] args)
    {
        var m = typeof(RecordPatches).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException($"RecordPatches.{method} not found — this test drives it.");
        try { return m.Invoke(null, args); }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            // Rethrow the real exception with its stack, so Assert.Throws sees the type and the
            // message the production code produced rather than a reflection wrapper.
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
            throw; // unreachable
        }
    }
}
