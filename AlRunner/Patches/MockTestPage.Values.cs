// MockTestPage.Values.cs — the string↔NavValue conversions a TestPage control needs:
// options, min/max bounds, numerics, booleans and temporals.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AlRunner.Patches;
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;
using Microsoft.Dynamics.Nav.Types.Data;
using Microsoft.Dynamics.Nav.Types.Exceptions;

namespace AlRunner;
/// <summary>
/// Option values as a TestPage sees them: member NAMES going in, a member name coming back out.
///
/// AL's TestPage API is string-typed for every control — <c>Field.SetValue('Sum')</c>,
/// <c>Field.Value()</c> — so the option's member table is the only thing that can turn that
/// string into the ordinal the record stores, and back. Without it a write puts a NavText into
/// an Option and dies inside BC's own setter ("The value \"Sum\" can't be evaluated into type
/// Option"), and a read answers with the bare ordinal, which no AL test is written against.
///
/// Shared by the Rec-bound field and the page-variable-bound field. It was originally written
/// for the latter only, which is exactly the shape of bug worth avoiding here: the two kinds of
/// control look identical in AL, so a test author has no way to know that one of them resolves
/// option names and the other does not.
/// </summary>
internal static class TestPageOptionValue
{
    /// <summary>Turn the string a test wrote into the NavOption the binding holds.</summary>
    internal static NavValue Resolve(NavOption current, string value, string[]? captions, string context)
    {
        var metadata = current.NavOptionMetadata
            ?? throw TestPageShapeGap.OptionValue(
                context,
                "the control is bound to an Option with no option metadata, so a value cannot "
                + "be resolved by name");

        var options = Members(metadata);
        var ordinals = Ordinals(metadata);

        // A TestPage sets an option by what the user sees, i.e. the control's OptionCaption,
        // which is NOT the option's member names (Pageworks: captions
        // "Fields,Blocks,Images,…" over members [Field, Block, Image, …]). Captions first,
        // then members — the caption is what AL test code is written against.
        if (captions != null)
            for (var i = 0; i < captions.Length; i++)
                if (OptionNamesEqual(captions[i], value))
                    return NavOption.Create(metadata, OrdinalAt(ordinals, i));

        // Issue #1928, decided against real-BC evidence (StefanMaron/BusinessCentral.AL.
        // Language.Tests#50, run against a real BC service tier on two BC versions): an
        // Enum-typed control's TestPage.SetValue resolves ONLY by the declared Caption and
        // REFUSES the member name — SetValue('Block') against `value(1; Block) { Caption =
        // 'Blocks'; }` throws "Your entry of 'Block' is not an acceptable value for
        // 'Kind'.", not a successful set. So for an Enum-backed metadata (IsEnum), the
        // member-name fallback below must NOT run — accepting a spelling real BC rejects is
        // exactly the silent divergence loud-failures.md forbids, and it is what shipped as
        // a ghost test in tests/runner-extras/page-enum-control-modal before this fix.
        //
        // The plain `Option` primitive is a SEPARATE, unverified question — no real-BC
        // evidence either way distinguishes caption-vs-member resolution for it, so its
        // historical member-name fallback stays as-is; only Enum's is removed here.
        var isEnumBacked = metadata.IsEnum;
        if (!isEnumBacked)
            for (var i = 0; i < options.Length; i++)
                if (OptionNamesEqual(options[i], value))
                    return NavOption.Create(metadata, OrdinalAt(ordinals, i));

        // A bare number is a legal way to set an option, and unambiguous.
        if (int.TryParse(value, System.Globalization.NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var literal)
            && (ordinals == null || ordinals.Contains(literal)))
            return NavOption.Create(metadata, literal);

        throw new AlRunner.Infrastructure.RunnerOutOfScopeException(
            context,
            isEnumBacked
                ? $"testpage-option-value — '{value}' is not an acceptable value. An "
                  + "Enum-typed control resolves TestPage.SetValue by its declared Caption "
                  + "only, never by the member name (real BC's own behavior — see issue "
                  + "#1928) — "
                  + (captions != null
                      ? $"acceptable captions are [{string.Join(", ", captions)}]"
                      : "the enum declares no captions")
                  + $". Member names ([{string.Join(", ", options)}]) are NOT accepted. "
                  + "See docs/scope.md"
                : $"testpage-option-value — '{value}' is not one of the option's values "
                  + $"[{string.Join(", ", options)}]"
                  + (captions != null
                      ? $" nor one of its captions [{string.Join(", ", captions)}]"
                      : " (the control declares no OptionCaption)")
                  + ". See docs/scope.md");
    }

    /// <summary>
    /// The text a test reads back. Deliberately the same spelling <see cref="Resolve"/> accepts
    /// first, so <c>SetValue(Value())</c> is a no-op — a page whose read and write disagreed
    /// about captions-vs-members would let a test copy a value from one field to another and
    /// silently write a different member.
    /// </summary>
    internal static string? Display(NavOption option, string[]? captions)
        => option.NavOptionMetadata is { } metadata
            ? DisplayOrdinal(metadata, option.Value, captions)
            : null;

    /// <summary>
    /// The same text <see cref="Display"/> produces, for a BARE ORDINAL rather than for a
    /// NavOption that already carries its own metadata (issue #2367).
    ///
    /// <c>NavTestField.ALAssertEquals</c> and <c>ALSetValue</c> — the real, precompiled BC
    /// methods the AL compiler emits for <c>TestPage.&lt;field&gt;.AssertEquals(&lt;option&gt;)</c>
    /// and <c>SetValue(&lt;option&gt;)</c> — never hand an AL Option/Enum value to
    /// <see cref="ITestField"/> as-is. They round-trip it through
    /// <c>NavValue.CreateNavValueFromObject(NavValueMetadata.DefaultMetadata(FieldType), value)</c>,
    /// whose <c>NavNclType.NavOption</c> arm rebuilds the value against the DEFAULT option
    /// metadata, and then hand the resulting <c>ClientObject</c> — a bare ordinal, with the
    /// field's own member/caption table gone — to <see cref="ITestField.ValueToString"/>.
    ///
    /// So <c>ValueToString</c> is where the control's own option table has to be put back.
    /// The metadata comes from the value the control currently holds, which is the control's
    /// option set; only the ordinal comes from the caller.
    /// </summary>
    internal static string? DisplayOrdinal(NavOption? current, object? value, string[]? captions)
        => current?.NavOptionMetadata is { } metadata && TryAsOrdinal(value, out var ordinal)
            ? DisplayOrdinal(metadata, ordinal, captions)
            : null;

    private static string? DisplayOrdinal(object metadata, int ordinal, string[]? captions)
    {
        var index = IndexOfOrdinal(metadata, ordinal);
        if (index < 0) return null;

        if (captions != null && index < captions.Length) return captions[index];
        var options = Members(metadata);
        return index < options.Length ? options[index] : null;
    }

    // Deliberately narrow. An ordinal is what BC's own round trip leaves behind for an
    // Option/Enum, and nothing else on this path is one: a string is not accepted here
    // because ALSetValue's `value is NavStringValue` fast path never reaches ValueToString,
    // so a string arriving would mean some OTHER caller with an unexamined contract, and
    // silently reinterpreting its text as an option would be exactly the kind of guess
    // loud-failures.md exists to prevent. Anything unrecognised falls back to the caller's
    // own Convert.ToString, i.e. to the behaviour before #2367.
    private static bool TryAsOrdinal(object? value, out int ordinal)
    {
        switch (value)
        {
            case NavOption option: ordinal = option.Value; return true;
            case int i:            ordinal = i;            return true;
            case short s:          ordinal = s;            return true;
            case byte b:           ordinal = b;            return true;
            case sbyte sb:         ordinal = sb;           return true;
            case long l when l >= int.MinValue && l <= int.MaxValue:
                                   ordinal = (int)l;       return true;
            default:               ordinal = 0;            return false;
        }
    }

    /// <summary>
    /// An Enum-typed control's per-value captions, sourced from the enum's OWN metadata.
    ///
    /// Unlike the <c>Option</c> primitive, an AL <c>Enum</c> has no page-level
    /// <c>OptionCaption</c> property to declare, so
    /// <see cref="AlRunner.Patches.RunnerPageInstance.TryGetOptionCaptions"/>'s
    /// <c>ControlDefinition.OptionCaptionML</c> lookup is always empty for it (verified via
    /// <c>AL_RUNNER_TRACE_PAGE_METADATA=2</c> against an Enum-bound page-variable control:
    /// <c>OptionCaption='' OptionCaptionML=''</c>). Real BC computes an Enum's captions
    /// from its own metadata instead — see issue #1928's real-BC evidence: a real service
    /// tier's <c>TestPage.SetValue</c> on an Enum control resolves by the declared
    /// <c>Caption</c> and REFUSES the member name (the exact opposite of what this runner
    /// did before this fix).
    ///
    /// <c>IsEnum</c>/<c>GetOrdinals()</c>/<c>GetCaptionFromIndex(int)</c> are public virtuals
    /// on <c>NCLOptionMetadata</c> (decompiled: <c>Microsoft.Dynamics.Nav.Ncl.dll</c>), which
    /// <c>AlEnumOptionMetadata</c> (EnumMetadataPatches.cs) overrides from the SAME
    /// emit-captured <c>(name, options[], indexes[], captions[])</c> tuple already used, and
    /// already accepted as faithful, for <c>Enum::"X".Ordinals()/.Names()</c> via
    /// <c>NCLEnumMetadata_CreateByIdAlAware</c>. The result is built in
    /// <c>GetOrdinals()</c> order, which is the SAME order <see cref="Ordinals"/>'s reflection
    /// (over a different, private accessor) already returns for the same metadata instance —
    /// both walk the one <c>(options[], indexes[])</c> pair the AL emit captured — so a
    /// caption at index i here lines up with the member at index i in <see cref="Members"/>,
    /// which is what <see cref="Resolve"/> and <see cref="Display"/> index into.
    ///
    /// Returns null for a plain <c>Option</c> value (<c>IsEnum</c> is false there) or when
    /// no bound value is available — the caller falls back to member-name display/resolution,
    /// same as when a control declares no <c>OptionCaption</c> at all.
    /// </summary>
    internal static string[]? EnumCaptions(NavOption? option)
    {
        if (option?.NavOptionMetadata is not { IsEnum: true } metadata) return null;

        var ordinals = new System.Collections.Generic.List<int>();
        foreach (var ordinal in metadata.GetOrdinals()) ordinals.Add(ordinal);

        var captions = new string[ordinals.Count];
        for (var i = 0; i < ordinals.Count; i++)
            captions[i] = metadata.GetCaptionFromIndex(ordinals[i]);
        return captions;
    }

    /// <summary>The number of members, for AL that walks an option set rather than naming one.</summary>
    internal static int Count(NavOption option)
        => option.NavOptionMetadata is { } metadata ? Members(metadata).Length : 0;

    /// <summary>The member at a position, in the same spelling <see cref="Display"/> uses.</summary>
    internal static string MemberAt(NavOption option, int index, string[]? captions)
    {
        if (captions != null && index >= 0 && index < captions.Length) return captions[index];
        if (option.NavOptionMetadata is not { } metadata) return string.Empty;
        var options = Members(metadata);
        return index >= 0 && index < options.Length ? options[index] : string.Empty;
    }

    // Options / OrdinalValues are internal to Ncl — read them reflectively rather than
    // re-deriving the option set from OptionString, which would lose the ordinal gaps a
    // declared option set is allowed to have.
    private static string[] Members(object metadata)
        => ReadNonPublic<string[]>(metadata, "Options") ?? Array.Empty<string>();

    private static int[]? Ordinals(object metadata) => ReadNonPublic<int[]>(metadata, "OrdinalValues");

    private static int OrdinalAt(int[]? ordinals, int index)
        => ordinals != null && index < ordinals.Length ? ordinals[index] : index;

    private static int IndexOfOrdinal(object metadata, int ordinal)
    {
        var ordinals = Ordinals(metadata);
        if (ordinals == null)
            return ordinal >= 0 && ordinal < Members(metadata).Length ? ordinal : -1;
        return Array.IndexOf(ordinals, ordinal);
    }

    private static T? ReadNonPublic<T>(object target, string name) where T : class
    {
        for (var t = target.GetType(); t != null; t = t.BaseType)
        {
            var pi = t.GetProperty(name, System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.DeclaredOnly);
            if (pi != null) return pi.GetValue(target) as T;
        }
        return null;
    }

    // AL option names are compared ignoring case and spacing, the same way the runner
    // compares object and field names elsewhere ("Custom Fields" vs "CustomFields").
    private static bool OptionNamesEqual(string left, string right)
        => string.Equals(left.Replace(" ", string.Empty), right.Replace(" ", string.Empty),
            StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Enforces a field's declared <c>MinValue</c>/<c>MaxValue</c> AL properties on a TestPage
/// control write (issue #2495). Measured against real BC (28.1 / 28.4, see #2490's arm A2):
/// a Decimal field with <c>MinValue = 0;</c> raises
/// <c>Validation error for Field: &lt;caption&gt;,  Message = 'The value must be greater than
/// or equal to 0. Value: -1.00. (Select Refresh to discard errors)'</c> from a TestPage
/// SetValue, while the SAME write via <c>Rec.Validate</c> or a plain field assignment raises
/// nothing at all — this is a client/page-layer check, not a table-trigger one, so it must
/// stay out of NavRecord.ALValidateAsync (which Rec.Validate also calls).
///
/// <para>Since #2900 this raises only the CORE of that message. Two layers are added around it
/// by code that is not this helper's: <see cref="TestFieldValidationErrors"/> appends
/// <c>" (Select Refresh to discard errors)"</c> when it records the refusal (BC's client does
/// that, measured on corpus run 34002487601), and BC's own <c>NavTestField.CheckError</c> then
/// wraps the result in <c>Validation error for Field: &lt;name&gt;,  Message = '…'</c>. The
/// AL-visible string is the same one #2490 measured; it is now composed rather than
/// assembled here, which is what stops the suffix appearing twice.</para>
///
/// <para>Only numeric field types are checked — MinValue/MaxValue is meaningless on Text/Code/
/// Boolean/etc., and AL does not let those types declare it.</para>
///
/// <para>The bound text is read via <see cref="RecordPatches.TryGetParsedFieldMinMax"/> — the
/// parse-time source, not the constructed <c>NCLMetaField</c> — because NCLMetaField
/// (Microsoft.Dynamics.Nav.Runtime) does not expose MinValue/MaxValue on the built runtime
/// object at all (confirmed empirically: no Min/MaxValue-named member of any accessibility),
/// even though <c>MetaField</c> (Microsoft.Dynamics.Nav.Types.Metadata, what the runner's
/// NclMetaTableBuilder constructs FROM) does carry them. Same rationale as
/// <see cref="RecordPatches.TryGetParsedFieldCaption"/> reading Caption straight from the parsed
/// table rather than through NCLMetaField's own getter.</para>
/// </summary>
internal static class TestPageMinMaxValue
{
    internal static void Check(NCLMetaTable table, int fieldNo, NavType fieldType, string rawValue, string caption)
    {
        if (fieldType is not (NavType.Decimal or NavType.Integer or NavType.BigInteger)) return;
        if (!table.TryGetFieldByNo(fieldNo, out var field) || field == null) return;

        var (minText, maxText) = RecordPatches.TryGetParsedFieldMinMax(table.TableId, fieldNo);
        if (string.IsNullOrEmpty(minText) && string.IsNullOrEmpty(maxText)) return;

        // AL's TestPage SetValue is string-typed for every control, and the generic
        // ALCompiler.ToNavValue(value) path taken above (there is no per-type conversion for a
        // Decimal/Integer control here, unlike the Boolean special-case) always produces a
        // NavText — so the bound check parses the RAW string the test wrote, exactly as BC's own
        // client-side field validation would before ever constructing a typed NavValue.
        if (!decimal.TryParse(rawValue, NumberStyles.Any, CultureInfo.InvariantCulture, out var value)) return;

        var isInteger = fieldType is NavType.Integer or NavType.BigInteger;

        if (!string.IsNullOrEmpty(minText) && decimal.TryParse(minText, NumberStyles.Any, CultureInfo.InvariantCulture, out var min)
            && value < min)
            throw MakeError(isInteger, "greater than or equal to", minText!, value);

        if (!string.IsNullOrEmpty(maxText) && decimal.TryParse(maxText, NumberStyles.Any, CultureInfo.InvariantCulture, out var max)
            && value > max)
            throw MakeError(isInteger, "less than or equal to", maxText!, value);
    }

    // The offending VALUE renders with the field's decimal places (2 by default for a Decimal,
    // none for an Integer/BigInteger — measured against real BC, #2490's arm A2: "-1.00"). The
    // BOUND is echoed as BC declared it (the raw MinValue/MaxValue AL text, e.g. "0"), NOT
    // reformatted to the field's decimal places — also measured in #2490's arm A2, where the
    // bound reads "0" while the value reads "-1.00" for the identical Decimal field.
    private static string FormatValue(decimal d, bool isInteger)
        => isInteger ? d.ToString("0", CultureInfo.InvariantCulture)
                     : d.ToString("0.00", CultureInfo.InvariantCulture);

    private static System.Exception MakeError(bool isInteger, string comparison, string boundText, decimal value)
    {
        // The BARE message only. BC's own NavTestField.CheckError wraps whatever an ITestField
        // records into "Validation error for Field: {Name},  Message = '{recorded}'" using
        // Lang.TestValidationException, so building that wrapper here too would double it
        // (#2900). The AL-visible string is unchanged; it is composed one layer out now.
        var msg = $"The value must be {comparison} "
            + $"{boundText}. Value: {FormatValue(value, isInteger)}.";

        var t = System.Type.GetType(
            "Microsoft.Dynamics.Nav.Types.Exceptions.NavNCLDialogException, Microsoft.Dynamics.Nav.Types");
        if (t != null)
        {
            var ctor = t.GetConstructor(new[] { typeof(string) });
            if (ctor != null) return (System.Exception)ctor.Invoke(new object[] { msg });
        }
        return new System.InvalidOperationException(msg);
    }
}

/// <summary>
/// A Decimal-typed control always renders with the field's decimal places -- default 2,
/// the same convention #2490 measured against real BC and <see cref="TestPageMinMaxValue.FormatValue"/>
/// already codifies for the error-message text -- regardless of how many decimal digits the
/// underlying .NET <c>decimal</c>'s own <c>Scale</c> happens to carry (issues #2634 / #2534).
///
/// A record field reaches its stored value through <see cref="Microsoft.Dynamics.Nav.Runtime.NavRecord"/>'s
/// own Validate/Insert path, which is BC's own precompiled code and out of this runner's
/// control -- so a Rec-bound Decimal control can format correctly today by construction, if
/// BC's own write path already normalises the stored scale. A page-GLOBAL <c>Decimal</c>
/// (<c>Values: array[20] of Decimal;</c>) gets no such round trip at all: a plain assignment
/// like <c>Values[i] := i * 10;</c> stores a .NET decimal with Scale 0, and
/// <c>Convert.ToString(10m)</c> answers "10" where real BC's page layer answers "10.00". Both
/// <see cref="LiveNavTestField.Value"/> and <see cref="PageVariableTestField.Value"/> read
/// through this helper so neither binding shape can silently regress relative to the other --
/// exactly the LiveNavTestField/PageVariableTestField pairing pattern <see cref="TestPageBooleanValue"/>
/// and <see cref="TestPageOptionValue"/> already use.
///
/// Only <see cref="NavDecimal"/> needs special handling: <see cref="NavInteger"/> and
/// <see cref="NavBigInteger"/> already round-trip correctly through
/// <c>Convert.ToString</c> because an integral CLR type never carries a fractional Scale to
/// lose in the first place -- matching the "0" (no decimals) half of
/// <see cref="TestPageMinMaxValue.FormatValue"/>'s own convention without any code needed here.
///
/// <para><b>The number of decimals is the CONTROL's, not a constant (#3406).</b> This helper
/// used to apply <c>"0.00"</c> to every Decimal on every page, so a control declaring
/// <c>DecimalPlaces = 3 : 3</c> or <c>AutoFormatType = 11</c> read back with two decimals and
/// nothing said so. The format string is BC's own: <c>NavForm.GetDecimalString</c> runs to
/// completion inside the runner and publishes its result as the control's
/// <c>Control&lt;id&gt;_Format</c> source expression, which
/// <see cref="RunnerPageInstance.TryGetControlFormat"/> reads. See
/// docs/limitations.md#testpage-decimal-formatting for the measured format strings and what
/// still is not covered.</para>
/// </summary>
internal static class TestPageNumericValue
{
    // The two-decimal spelling used when the control has no format of its own — a
    // record-only field, or a page whose format expression could not be read. Historical:
    // every existing assertion in the corpus and in Microsoft's own buckets is written
    // against it, so this arm must not move.
    private const string NoControlFormat = "0.00";

    /// <param name="controlFormat">
    /// The .NET format string BC's own <c>GetDecimalString</c> cascade computed for this
    /// control, or null/empty when there is none to read. See the class remarks: this is
    /// BC's answer, not the runner's — the runner only applies it.
    /// </param>
    internal static string? Format(NavValue? navValue, string? controlFormat = null)
    {
        if (navValue is not NavDecimal d) return null;

        var value = Convert.ToDecimal(d.ClientObject, CultureInfo.InvariantCulture);
        if (string.IsNullOrEmpty(controlFormat))
            return value.ToString(NoControlFormat, CultureInfo.InvariantCulture);

        // .NET does not reject an unrecognised custom format — it treats the whole string as
        // a literal and answers it back verbatim, which would put the format string itself
        // where a number belongs. That is a silent wrong answer of exactly the kind #3406 is
        // about, so detect it (no digit in the output) and fall back rather than ship it.
        var formatted = value.ToString(controlFormat, CultureInfo.InvariantCulture);
        foreach (var c in formatted)
            if (char.IsDigit(c)) return formatted;

        return value.ToString(NoControlFormat, CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// How a BLANK temporal control renders: the empty string, for Date, Time and DateTime alike.
///
/// <para>Issue #2361. The runner fell through to <c>Convert.ToString(ObjectValue)</c>, which
/// renders the underlying <c>DateTime</c> and so answered <c>01/01/0001 00:00:00</c> where real
/// BC answers <c>''</c> — the same string for all three types, because all three wrap one
/// <c>DateTime</c> (see below). Microsoft's own <c>UserCardTest.GenerateWebServiceKeyNoExpires</c>
/// asserts <c>AssertEquals('')</c> on page 9807's blank <c>WebServiceExpiryDate</c>.</para>
///
/// <para><b>The rule is read out of BC, not inferred.</b> Each of BC's three temporal formatters
/// opens with the identical guard — <c>NavDateFormatter</c>, <c>NavTimeFormatter</c> and
/// <c>NavDateTimeFormatter.FormatWithFormatNumber</c> in <c>Ncl.dll</c>, all three:</para>
/// <code>
///   if (navX.IsZeroOrEmpty) return string.Empty;
/// </code>
/// <para>and <c>NavDateTimeValue.IsZeroOrEmpty</c> — the base class of all of <c>NavDate</c>,
/// <c>NavTime</c> and <c>NavDateTime</c> — is <c>Value == NavDateTimeHelper.DateTimeUndefined</c>,
/// where <c>DateTimeUndefined</c> is <c>default(DateTime)</c>. So the predicate below is BC's
/// own, spelled the same way.</para>
///
/// <para><b>Why <c>default(DateTime)</c> is never a legitimate value to suppress.</b> BC's
/// smallest representable Date is <c>DateTimeMinimum</c> = <c>0001-01-02</c>, one day after the
/// CLR minimum, and its C/SIDE encoding maps 0 to undefined and 1 to that minimum. A real
/// temporal therefore cannot BE <c>default(DateTime)</c>, so blanking it loses nothing —
/// which is what lets the populated half of the corpus suite keep passing.</para>
///
/// <para>Read through by BOTH <see cref="LiveNavTestField"/> (Rec-bound) and
/// <see cref="PageVariableTestField"/> (page-global), the same pairing
/// <see cref="TestPageBooleanValue"/> and <see cref="TestPageOptionValue"/> already use, so
/// neither binding shape can drift from the other — the corpus suite asserts both.</para>
///
/// <para>It must also be what <c>ValueToString</c> answers, for the reason spelled out in
/// <see cref="TestPageBooleanValue"/>: <c>NavTestField.ALAssertEquals</c> converts the EXPECTED
/// value through <c>ValueToString</c> and compares it ORDINALLY against the getter, so moving
/// the getter alone would leave <c>AssertEquals('')</c> failing — which is a distinct test in
/// the corpus suite and was failing in exactly that way before this fix.</para>
/// </summary>
internal static class TestPageBlankTemporalValue
{
    /// <summary>The stored NavValue's rendering — <c>""</c> when blank, otherwise null to let
    /// the caller's existing chain render it.</summary>
    internal static string? Format(NavValue? navValue)
        => navValue is NavDateTimeValue t && t.IsZeroOrEmpty ? string.Empty : null;

    /// <summary>The same rule for an already-unwrapped CLR value, as ValueToString sees it —
    /// <c>ClientObject</c> on all three types hands back the bare <c>DateTime</c>.</summary>
    internal static string? FormatObject(object? value)
        => value is DateTime dt && dt == default ? string.Empty : null;
}

/// <summary>
/// Boolean values as a TestPage sees them, on either shape of control: a page-variable-bound
/// one (<c>field(Flag; ShowFlag)</c> where <c>ShowFlag: Boolean</c>) or a Rec-bound one
/// (<c>field(Flag; Rec.Flag)</c> where the source table field is <c>Boolean</c>) — see issue
/// #1870, the Rec-bound half of #1837 that #1869 (the page-variable half) left open.
///
/// <c>NavTestField.ALSetValue</c> — the real, precompiled BC method the AL compiler emits for
/// every <c>TestPage.&lt;field&gt;.SetValue(&lt;Boolean&gt;)</c> call — never hands a NavValue
/// straight to <see cref="ITestField"/>. For anything that is not itself already a
/// <c>NavStringValue</c> it round-trips through <see cref="ITestField.FieldType"/> (to pick a
/// <c>NavValueMetadata</c>) and then <see cref="ITestField.ValueToString"/> (both OUR OWN mock
/// methods) to turn the boolean back into a string before ever reaching <see cref="ITestField.Value"/>'s
/// setter — see the doc comment on <see cref="PageVariableTestField.FieldType"/> for why that
/// matters here. <see cref="LiveNavTestField.FieldType"/> is sourced from the source table
/// field's own declared type instead, but reaches the same <c>NavType.Boolean</c> answer for a
/// <c>Boolean</c> field, so the round trip is identical on both sides.
///
/// Because both ends of that round trip are code THIS runner owns (<see cref="ITestField.ValueToString"/>
/// always answers with <c>Convert.ToString(boolValue)</c>, i.e. exactly "True" or "False"), accepting
/// only that spelling here is not a narrowing of what <c>SetValue(&lt;Boolean&gt;)</c> can express —
/// it is the ONLY spelling that overload ever produces. Anything else (a literal
/// <c>SetValue('Yes')</c>, locale spellings, ...) is a genuinely separate, upstream-unvalidated
/// question about what real BC's own text-to-Boolean evaluate accepts on this surface, so it stays
/// out of scope here and throws loudly rather than guessing.
/// </summary>
internal static class TestPageBooleanValue
{
    /// <summary>
    /// How a Boolean control RENDERS. Issue #2795: real BC answers "Yes"/"No", measured on all
    /// eight BC legs of the corpus CI (27.0 through 28.4, run 33967745688 on corpus PR #150 —
    /// <c>Actual:&lt;Yes&gt;</c> on every failing leg, no other value anywhere in the run) and
    /// pinned upstream by <c>BooleanFieldControl_ReadsAsYesOrNo</c>. The runner answered
    /// <c>Convert.ToString(bool)</c>, i.e. "True"/"False".
    ///
    /// <para>Read through by BOTH <see cref="LiveNavTestField"/> (a Rec-bound control) and
    /// <see cref="PageVariableTestField"/> (a page-global one), the same pairing
    /// <see cref="TestPageNumericValue"/> and <see cref="TestPageOptionValue"/> already use, so
    /// neither binding shape can drift from the other.</para>
    ///
    /// <para>It is also what <c>ValueToString</c> must answer, and that is not a nicety.
    /// <c>NavTestField.ALAssertEquals</c> — BC's own precompiled method — converts a non-string
    /// expected value through <c>testField.ValueToString</c> and then compares it ORDINALLY
    /// against the control's value:</para>
    /// <code>
    ///   value = NavValue.CreateNavValueFromObject(NavValueMetadata.DefaultMetadata(testField.FieldType), value);
    ///   text  = testField.ValueToString(value.ClientObject);
    ///   if (string.CompareOrdinal(ALValue, text) != 0) throw ...
    /// </code>
    /// <para>So changing the getter alone would have broken every
    /// <c>AssertEquals(&lt;Boolean&gt;)</c> — "Yes" against "True" — which passes today only
    /// because both halves are wrong in the same way.</para>
    /// </summary>
    internal static string? Format(NavValue? navValue)
        => navValue is NavBoolean b
            ? (Convert.ToBoolean(b.ClientObject, CultureInfo.InvariantCulture) ? "Yes" : "No")
            : null;

    /// <summary>The same rendering for an already-unwrapped CLR value, as ValueToString sees it.</summary>
    internal static string? FormatObject(object? value)
        => value is bool b ? (b ? "Yes" : "No") : null;

    /// <summary>
    /// The inverse: the text a TestPage write carries, back to a Boolean.
    ///
    /// <para>Accepts "Yes"/"No" ONLY. That is what <see cref="FormatObject"/> now produces, so
    /// <c>SetValue(&lt;Boolean&gt;)</c> round-trips through it — see the chain in
    /// <c>NavTestField.ALSetValue</c>, where a non-string value goes out through
    /// <c>ValueToString</c> and comes back in through this.</para>
    ///
    /// <para><b>"True"/"False" is refused, and that is measured, not assumed.</b> An earlier
    /// version of this fix accepted it, reasoning that it is the spelling AL's own
    /// <c>Evaluate</c> takes for a Boolean. Corpus PR #163 put the question in front of a
    /// service tier and all eight BC legs answered identically:</para>
    /// <code>
    ///   Validation error for Field: RecTrue,  Message = 'Your entry of 'False' is not an
    ///   acceptable value for 'Rec True'. (Select Refresh to discard errors)'
    /// </code>
    /// <para>So this is not an unsupported surface the runner should refuse as out of scope —
    /// BC has a defined answer for it, and the runner's job is to give the same one. Hence a
    /// validation error in BC's own shape rather than a <c>RunnerOutOfScopeException</c>, built
    /// the same way <see cref="TestPageMinMaxValue.MakeError"/> already builds that shape for a
    /// MinValue/MaxValue refusal.</para>
    ///
    /// <para>One fidelity gap, stated rather than hidden: BC puts the control's declared NAME in
    /// the <c>Field:</c> slot and its CAPTION in the quoted target ("RecTrue" and "Rec True"
    /// above). This runner's <c>ITestField.Name</c> answers the caption, so both slots read the
    /// caption here. A test asserting the message as a substring — as the corpus one does — is
    /// unaffected; one asserting it verbatim would see the difference.</para>
    /// </summary>
    internal static NavValue Resolve(string value, string caption)
    {
        if (string.Equals(value, "Yes", StringComparison.OrdinalIgnoreCase)) return NavBoolean.Create(true);
        if (string.Equals(value, "No", StringComparison.OrdinalIgnoreCase)) return NavBoolean.Create(false);

        throw MakeNotAcceptableError(value, caption);
    }

    /// <summary>
    /// BC's own refusal for a value a control will not take:
    /// <c>Your entry of '{value}' is not an acceptable value for '{caption}'.</c>
    /// <para>Two layers are deliberately absent here (#2900), because neither is this helper's
    /// to add: <see cref="TestFieldValidationErrors"/> appends
    /// <c>" (Select Refresh to discard errors)"</c> when it records the refusal, and BC's own
    /// <c>NavTestField.CheckError</c> then wraps it in <c>Validation error for Field: {name},
    /// Message = '…'</c> — including the double space after the comma, which is BC's and not a
    /// typo. The composed result is the string corpus PR #163 measured on all eight legs.</para>
    /// </summary>
    private static System.Exception MakeNotAcceptableError(string value, string caption)
    {
        // The BARE message only — see TestPageMinMaxValue.MakeError for why the
        // "Validation error for Field: ..." wrapper is BC's to add and no longer ours (#2900).
        var msg = $"Your entry of '{value}' "
            + $"is not an acceptable value for '{caption}'.";

        var t = System.Type.GetType(
            "Microsoft.Dynamics.Nav.Types.Exceptions.NavNCLDialogException, Microsoft.Dynamics.Nav.Types");
        if (t != null)
        {
            var ctor = t.GetConstructor(new[] { typeof(string) });
            if (ctor != null) return (System.Exception)ctor.Invoke(new object[] { msg });
        }
        return new System.InvalidOperationException(msg);
    }
}

/// <summary>
/// The Date / DateTime / Time a TestPage control was handed, as a typed NavValue (#3384).
///
/// <para>AL's TestPage surface is string-typed for every control, so a temporal value reaches a
/// control as text by two routes and both end here. A typed AL argument
/// (<c>SetValue(&lt;Date&gt;)</c>) is rendered to text by BC's own <c>NavTestField.ALSetValue</c>
/// through <see cref="ITestField.ValueToString"/> before the control ever sees it; text the test
/// wrote itself (<c>SetValue(Format(D))</c>, <c>SetValue('2026-01-15')</c>) arrives unchanged.</para>
///
/// <para>Step two is BC's own client-side evaluator rather than a reimplementation of it, which
/// is what makes the spellings accepted here the spellings a real service tier accepts.</para>
/// </summary>
internal static class TestPageTemporalValue
{
    /// <summary>
    /// Interpret <paramref name="value"/> as <paramref name="type"/>, or answer false and leave
    /// the caller on its normal <c>NavText</c> path — where BC raises its own refusal, which is
    /// the message a test asserting a rejected value is written against.
    /// </summary>
    internal static bool TryResolve(NavType type, string value, out NavValue? resolved)
    {
        resolved = null;
        if (type is not (NavType.Date or NavType.DateTime or NavType.Time)) return false;

        return TryResolveRoundTrip(type, value, out resolved)
            || TryEvaluateThroughBc(type, value, out resolved);
    }

    /// <summary>
    /// Step one: the spelling this runner's OWN <c>ValueToString</c> produces for a typed
    /// argument — <c>Convert.ToString(&lt;DateTime&gt;, InvariantCulture)</c>, the general
    /// date/time pattern <c>MM/dd/yyyy HH:mm:ss</c>. A <c>Time</c> arrives with
    /// <c>NavTime</c>'s base date attached ("01/02/0001 14:30:00"), which is why the Time arm
    /// reads <c>TimeOfDay</c> and drops the carrier date.
    ///
    /// <para>Handling it here rather than leaving it to BC is not a divergence, and that was
    /// measured rather than assumed (PR #3394 review): a real BC 28.4.53241.0 tier handed
    /// <c>SetValue('01/01/2024 00:00:00')</c> as TEXT on a Date control accepts it and stores
    /// <c>2024-01-01</c> — the same answer this branch gives. What this branch buys is that the
    /// typed-argument path does not depend on that, since BC's own client never emits this
    /// spelling for a Date or a Time.</para>
    ///
    /// <para><c>TryParseExact</c>, never a lenient parse, and that is the point of the arm:
    /// a lenient invariant parse would also swallow <c>15.01.28</c> and <c>011528</c> here and
    /// answer before BC's evaluator ever sees them, silently substituting .NET's reading of a
    /// user-typed date for the platform's. See <c>TestPageTemporalValueTests</c>.</para>
    /// </summary>
    internal static bool TryResolveRoundTrip(NavType type, string value, out NavValue? resolved)
    {
        resolved = null;
        if (type is not (NavType.Date or NavType.DateTime or NavType.Time)) return false;

        if (!DateTime.TryParseExact(value, "MM/dd/yyyy HH:mm:ss", CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var roundTrip))
            return false;

        // Create requires DateTimeKind.Local on all three — the ctors throw
        // NavNCLDateInvalidException otherwise, and TryParseExact answers Unspecified.
        resolved = type switch
        {
            NavType.Date => NavDate.Create(
                DateTime.SpecifyKind(roundTrip.Date, DateTimeKind.Local)),
            NavType.DateTime => NavDateTime.Create(
                DateTime.SpecifyKind(roundTrip, DateTimeKind.Local)),
            _ => NavTime.Create(DateTime.SpecifyKind(
                NavTimeBaseDate.Add(roundTrip.TimeOfDay), DateTimeKind.Local)),
        };
        return resolved != null;
    }

    // NavTime's ClientObject is a DateTime on BC's own base date; Create rejects anything else.
    private static readonly DateTime NavTimeBaseDate = new DateTime(1, 1, 2);

    private static bool _lookupDone;
    private static System.Reflection.MethodInfo? _getEvaluator;
    private static System.Reflection.MethodInfo? _evaluate;
    private static object? _trapError;
    private static Type? _navNclType;

    private static bool TryEvaluateThroughBc(NavType type, string value, out NavValue? resolved)
    {
        resolved = null;
        EnsureEvaluatorBound();

        var member = type switch
        {
            NavType.Date => "NavDate",
            NavType.DateTime => "NavDateTime",
            _ => "NavTime",
        };
        // Both pre-invoke steps THROW rather than decline (#3560). Declining sends the caller
        // to its NavText path, where BC raises its date-format refusal - so an AL author reads
        // BC rejecting the spelling they typed when what happened is that the runner could not
        // map this control's type onto this build's NavNclType at all.
        if (ForcedEnumMemberMissing.Value || !Enum.IsDefined(_navNclType!, member))
            throw new AlRunner.Infrastructure.BcShapeGapException(
                "TestPage SetValue on a Date/DateTime/Time control",
                "NavNclType." + member,
                $"this build's NavNclType does not define {member}, so the runner cannot name "
                + "the type whose evaluator it needs to read this control's value. A "
                + "runner/BC-version mismatch, not a rejected value.");

        var evaluator = ForcedNullEvaluator.Value
            ? null
            : _getEvaluator!.Invoke(null, new[] { Enum.Parse(_navNclType!, member) });
        if (evaluator == null)
            throw new AlRunner.Infrastructure.BcShapeGapException(
                "TestPage SetValue on a Date/DateTime/Time control",
                "NavValueEvaluator.GetEvaluator",
                $"GetEvaluator answered null for NavNclType.{member}, so this build declares "
                + "the type but hands the runner no evaluator for it and the runner has no way "
                + "to ask BC how it reads this value. A runner/BC-version mismatch, not a "
                + "rejected value.");

        // DataError.TrapError, not ThrowError: a spelling BC cannot read has to come back as
        // "no" so the caller keeps its NavText path, where BC raises the refusal AL is written
        // against. Throwing from here would replace that message with this one.
        var args = new object?[] { BcRuntime.SkeletonSession, _trapError, null, null, value, 0 };
        if (!InvokeEvaluate(() => (bool)_evaluate!.Invoke(evaluator, args)!)) return false;
        resolved = args[2] as NavValue;
        return resolved != null;
    }

    /// <summary>
    /// Run BC's evaluator and answer what it answered - but only for an answer BC gave.
    ///
    /// <para>Under <c>DataError.TrapError</c> a refusal is not an exception: BC returns false and
    /// traps its own <c>NavNCLEvaluateException</c> (28.1 Ncl, <c>NavValueEvaluator`1.Evaluate</c>:
    /// <c>catch when (obj is NavNCLEvaluateException &amp;&amp; dataError == 0)</c>; TrapError is
    /// 0). So anything else reaching here is the evaluator NOT COMPLETING, and answering false for
    /// it shows the AL author BC's date-format refusal for a value BC would have taken (#3444).</para>
    ///
    /// <para><c>BcShapeGapException</c> because an <c>asserterror</c> can absorb a
    /// <c>RunnerOutOfScopeException</c> and would then pass on a runner fault (#3062, #3428).</para>
    /// </summary>
    internal static bool InvokeEvaluate(Func<bool> invoke)
    {
        try { return invoke(); }
        catch (System.Reflection.TargetInvocationException ex)
            when (ex.InnerException is NavNCLEvaluateException)
        {
            // BC ran and said no, in the one shape that can still arrive as a throw.
            return false;
        }
        catch (System.Reflection.TargetInvocationException ex)
        {
            var inner = ex.InnerException ?? ex;
            throw new AlRunner.Infrastructure.BcShapeGapException(
                "TestPage SetValue on a Date/DateTime/Time control",
                "NavValueEvaluator.Evaluate",
                $"BC's own value evaluator did not complete ({inner.GetType().Name}: "
                + $"{inner.Message}), so the runner has no answer from BC about this spelling. "
                + "This is a runner fault on the evaluate path, not a value BC rejected.");
        }
        catch (Exception ex) when (ex is System.ArgumentException
                                or System.Reflection.TargetParameterCountException
                                or System.Reflection.TargetException)
        {
            // Unwrapped, so it came from Invoke itself rather than from BC: the bound Evaluate
            // does not take the arguments this call site passes. The two reflection types are
            // not ArgumentExceptions — both derive from ApplicationException — so before #3462
            // they propagated raw past every arm here and an asserterror absorbed them.
            throw new AlRunner.Infrastructure.BcShapeGapException(
                "TestPage SetValue on a Date/DateTime/Time control",
                "NavValueEvaluator.Evaluate",
                $"the bound Evaluate refused this call's arguments ({ex.GetType().Name}: "
                + $"{ex.Message}), so this BC build's evaluator is not the shape the runner "
                + "reflects against. A runner/BC-version mismatch, not a rejected value.");
        }
    }

    // Bound by reflection because NavValueEvaluator and NavNclType are internal to Ncl.dll. The
    // shape was read off the 28.1 and 28.4 decompiles; if a BC build moves it, the binding must
    // FAIL LOUDLY rather than quietly, because failing quietly is invisible: the typed-argument
    // arms keep working through the round-trip branch above and only the text spellings revert
    // to the pre-#3384 refusal. That is a silent downgrade of the kind loud-failures.md exists
    // to prevent, so it gets one line on stderr, once — not a throw, because declining still
    // leaves BC's own refusal as the observable outcome rather than a wrong value.
    private static bool TryBindEvaluator()
    {
        // Latched AFTER the work, under a gate: setting _lookupDone first let a second thread
        // read "already bound" while _evaluate was still null, and EnsureEvaluatorBound then
        // refused a BC build it had bound perfectly well, naming no reason (#3444, #3187's
        // latch-before-work shape).
        lock (BindGate)
        {
            if (_lookupDone) return _evaluate != null;
            try { return BindEvaluatorCore(); }
            finally { _lookupDone = true; }
        }
    }

    private static readonly object BindGate = new();

    private static bool BindEvaluatorCore()
    {
        string? why = null;
        try
        {
            var ncl = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "Microsoft.Dynamics.Nav.Ncl");
            var evaluatorType = ncl?.GetType("Microsoft.Dynamics.Nav.Runtime.NavValueEvaluator");
            _navNclType = ncl?.GetType("Microsoft.Dynamics.Nav.Runtime.NavNclType");
            var dataErrorType = typeof(Microsoft.Dynamics.Nav.Types.DataError);

            const System.Reflection.BindingFlags Any =
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance;

            if (ncl == null) why = "Microsoft.Dynamics.Nav.Ncl is not loaded";
            else if (evaluatorType == null) why = "NavValueEvaluator not found";
            else if (_navNclType is not { IsEnum: true }) why = "NavNclType not found, or not an enum";
            else if (!Enum.IsDefined(dataErrorType, "TrapError")) why = "DataError.TrapError not found";
            else
            {
                _getEvaluator = evaluatorType.GetMethod(
                    "GetEvaluator", Any, null, new[] { _navNclType }, null);
                // BcShape.FindMethod, not GetMethod(name, flags): the latter throws a bare
                // AmbiguousMatchException naming no member the moment BC ships a second
                // Evaluate, and NavMethodScope_AssertError rethrows only BcShapeGapException —
                // so under an AL asserterror that one would be ABSORBED and the asserterror
                // would pass (#3069). The signature cannot be pinned here because four of the
                // six parameter types are internal to Ncl, so this resolves by name and refuses
                // a second declaration by name, which is the outcome the ambiguity guard wants.
                var evaluate = AlRunner.Infrastructure.BcShape.FindMethod(
                    evaluatorType, "Evaluate", Any,
                    "TestPage SetValue on a Date/DateTime/Time control",
                    "NavValueEvaluator.Evaluate",
                    "the runner reads a control's typed date through BC's own value evaluator");

                if (_getEvaluator == null) why = "NavValueEvaluator.GetEvaluator(NavNclType) not found";
                else if (evaluate == null) why = "NavValueEvaluator.Evaluate not found";
                else if (evaluate.GetParameters().Length != 6)
                    why = $"NavValueEvaluator.Evaluate takes {evaluate.GetParameters().Length} "
                        + "parameters, expected 6";
                else
                {
                    _trapError = Enum.Parse(dataErrorType, "TrapError");
                    _evaluate = evaluate;
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            why = $"{ex.GetType().Name}: {ex.Message}";
        }

        _evaluate = null;
        _bindFailure = why;
        return false;
    }

    private static string? _bindFailure;

    // TEST SEAM (#3462). The bind result latches once per process, so a test cannot provoke a
    // real bind failure without destroying the binding for every other test in the run. This
    // makes EnsureEvaluatorBound refuse as though the bind had failed, without touching the
    // latch. AsyncLocal, not a plain static: xunit runs test classes in parallel and the
    // temporal suites drive this path concurrently.
    private static readonly System.Threading.AsyncLocal<string?> ForcedBindFailure = new();

    internal static IDisposable ForceBindFailure(string reason)
    {
        var previous = ForcedBindFailure.Value;
        ForcedBindFailure.Value = reason;
        return new ForcedBindFailureScope(previous);
    }

    // TEST SEAMS (#3560) for the two pre-invoke declines below EnsureEvaluatorBound. Neither is
    // reachable on a BC build the runner supports - both the enum member and its evaluator
    // resolve - so a refusal nobody can provoke would ship untested. AsyncLocal for the same
    // reason ForcedBindFailure is: the temporal suites run in parallel.
    private static readonly System.Threading.AsyncLocal<bool> ForcedEnumMemberMissing = new();
    private static readonly System.Threading.AsyncLocal<bool> ForcedNullEvaluator = new();

    internal static IDisposable ForceEnumMemberMissing() => Force(ForcedEnumMemberMissing);

    internal static IDisposable ForceNullEvaluator() => Force(ForcedNullEvaluator);

    private static IDisposable Force(System.Threading.AsyncLocal<bool> flag)
    {
        var previous = flag.Value;
        flag.Value = true;
        return new ForcedFlagScope(flag, previous);
    }

    private sealed class ForcedFlagScope : IDisposable
    {
        private readonly System.Threading.AsyncLocal<bool> _flag;
        private readonly bool _previous;
        internal ForcedFlagScope(System.Threading.AsyncLocal<bool> flag, bool previous)
        {
            _flag = flag;
            _previous = previous;
        }
        public void Dispose() => _flag.Value = _previous;
    }

    private sealed class ForcedBindFailureScope : IDisposable
    {
        private readonly string? _previous;
        internal ForcedBindFailureScope(string? previous) => _previous = previous;
        public void Dispose() => ForcedBindFailure.Value = _previous;
    }

    /// <summary>
    /// Bind BC's evaluator, or THROW — a failure to BIND means the runner cannot ask THIS BC
    /// build how it reads a date, which is a shape gap and not a decline. Declining would be
    /// invisible: the typed-argument path keeps working through <see cref="TryResolveRoundTrip"/>
    /// and only text spellings quietly revert to the pre-#3384 refusal.
    /// </summary>
    internal static void EnsureEvaluatorBound()
    {
        var forced = ForcedBindFailure.Value;
        if (forced == null && TryBindEvaluator()) return;

        // BcShapeGapException, not RunnerOutOfScopeException: an AL asserterror absorbs the
        // latter (NavMethodScope_AssertError rethrows only this type), so `asserterror
        // SetValue(<temporal>)` passed green on a runner that could not ask BC anything (#3462).
        throw new AlRunner.Infrastructure.BcShapeGapException(
            "TestPage SetValue on a Date/DateTime/Time control",
            "NavValueEvaluator.Evaluate",
            "could not bind BC's own NavValueEvaluator ("
            + (forced ?? _bindFailure ?? "reason not recorded") + "), so the runner cannot ask this BC "
            + "build how it reads a date, time or datetime a test typed as text. This is a "
            + "runner/BC-version mismatch, not a rejected value.");
    }
}
