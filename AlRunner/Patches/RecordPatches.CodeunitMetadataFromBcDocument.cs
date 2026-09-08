// RecordPatches.CodeunitMetadataFromBcDocument — four CodeUnit Metadata (2000000137)
// columns read out of BC's own emitted metadata document instead of being defaulted
// (issue #3606, one conversion of the chain #3562 tracks).
//
// THE ROUTE
//   BC's emitter hands CaptureOutputter a metadata document per object;
//   AlObjectMetadataRegistry keeps it keyed (kind, id) — "Codeunit" here, the AL
//   compiler's own SymbolKind name. This file reads four attributes off that document's
//   root <CodeUnit> element. It does NOT go through RunnerXmlMetadataLoader: that seam
//   serves BC's own NCLMeta* loaders, and nothing in this provider builds an NCLMetaCodeunit.
//   Reaching the registry directly is the same shape RecordPatches.NclMetaTableFromBcDocument
//   uses. See docs/codeunit-metadata-from-bc.md for the measurement behind every claim below.
//
// WHY BC'S DOCUMENT IS NOT PARSED BY BC HERE
//   Types.Metadata.MetaCodeunit(XmlNode) would parse the same document, but its output is a
//   MetaCodeunit, and this provider needs NavValues in a ReadOnlyRecordBuffer keyed by the
//   live metatable's own field names. Nothing converts one to the other, so the four
//   attributes are read directly. Which attributes and how they are typed comes FROM that
//   ctor, not from a guess — see the per-column notes.
//
// ATTRIBUTE-NAME TRAP: the compiler emits TestIsolation, BC's parser reads RequiredTestIsolation
//   The compiler writes TestIsolation="Disabled". MetaCodeunit's ctor switches on attribute
//   name and matches the string "RequiredTestIsolation" — a different name, so its own parse
//   never assigns the property and leaves it at 0 (None). This file reads the name the
//   COMPILER writes, because that is the value the declaration states; see the note on
//   ReadRequiredTestIsolationOrdinal for why that is the faithful answer and what would
//   settle it if BC ever changed either name.
using System.Xml.Linq;
using AlRunner.Infrastructure;
using Microsoft.Dynamics.Nav.Runtime;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    /// <summary>The AL compiler's own <c>SymbolKind</c> name for a codeunit, which is the
    /// kind half of <see cref="AlObjectMetadataRegistry"/>'s key.</summary>
    internal const string BcCodeunitMetadataKind = "Codeunit";

    /// <summary>
    /// The four columns of CodeUnit Metadata that BC's emitted document states, as read off
    /// one codeunit's document. A codeunit with no document — every codeunit in a precompiled
    /// dependency, and every codeunit on a compile-cache HIT with no replayed sidecar — has no
    /// <see cref="BcCodeunitDocumentValues"/> at all, and its row keeps BC's own column
    /// defaults rather than a value invented here.
    /// </summary>
    /// <param name="AlNamespace">The declaring file's namespace, or the empty string when it
    /// states none. BC emits the attribute either way, so an empty value is a stated empty
    /// rather than an absent attribute.</param>
    /// <param name="InherentPermissions">The permission-mask string BC's provider builds from
    /// the mask, e.g. "X". Empty when the codeunit declares none.</param>
    /// <param name="InherentEntitlements">Same, for the entitlement mask.</param>
    /// <param name="RequiredTestIsolationOrdinal">The ordinal of the column's own option set,
    /// already resolved. -1 when the document states nothing this column can carry.</param>
    internal sealed record BcCodeunitDocumentValues(
        string AlNamespace,
        string InherentPermissions,
        string InherentEntitlements,
        int RequiredTestIsolationOrdinal);

    /// <summary>
    /// BC's document for one codeunit, parsed into the four column values, or null when no
    /// document is registered for that id.
    /// </summary>
    internal static BcCodeunitDocumentValues? TryReadCodeunitMetadataDocument(
        int codeunitId, IReadOnlyDictionary<string, int>? isolationOrdinals)
    {
        if (!AlObjectMetadataRegistry.TryGet(BcCodeunitMetadataKind, codeunitId, out var xml)
            || string.IsNullOrEmpty(xml))
            return null;

        XElement root;
        try
        {
            root = XDocument.Parse(xml).Root
                   ?? throw CodeunitMetadataShapeGap(
                       $"BC's metadata document for codeunit {codeunitId} has no root element");
        }
        catch (System.Xml.XmlException ex)
        {
            // Refuse rather than fall back to the defaults. A document that is registered but
            // unparseable is a shape change in BC's emitter, and silently answering the old
            // defaults would hide it behind a green run (.claude/rules/loud-failures.md).
            throw CodeunitMetadataShapeGap(
                $"BC's metadata document for codeunit {codeunitId} is not well-formed XML: {ex.Message}");
        }

        return new BcCodeunitDocumentValues(
            Attr(root, "ALNamespace") ?? string.Empty,
            ReadPermissionMaskString(root, "InherentPermissions", codeunitId),
            ReadPermissionMaskString(root, "InherentEntitlements", codeunitId),
            ReadRequiredTestIsolationOrdinal(root, isolationOrdinals, codeunitId));
    }

    /// <summary>Attribute value ignoring the document's default namespace, which BC sets to
    /// <c>urn:schemas-microsoft-com:dynamics:NAV:MetaObjects</c> — an XML default namespace
    /// does not apply to attributes, so an unqualified name is the right lookup.</summary>
    private static string? Attr(XElement root, string name) => root.Attribute(name)?.Value;

    /// <summary>
    /// The five permission characters CodeUnit Metadata's two Text[5] mask columns are spelled
    /// with, in bit order: bit 0 Read, 1 Insert, 2 Modify, 3 Delete, 4 Execute. The indirect
    /// half of the mask (bits 5..9) uses the same letters lowercased.
    /// <para>Read out of Ncl.dll rather than assumed: <c>MetadataDataProvider.permissions</c> is
    /// a static <c>char[5]</c> whose initializer blob decodes to R, I, M, D, X (BC 28.1,
    /// 52 00 49 00 4D 00 44 00 58 00). <c>CreatePermissionMaskString</c> walks bits 0..4 and
    /// emits <c>permissions[n]</c> for a direct bit or <c>permissions[n] + 32</c> — the
    /// lowercase letter — for the matching indirect bit at n+5.</para>
    /// </summary>
    private const string PermissionMaskLetters = "RIMDX";

    /// <summary>
    /// One inherent-permission column, spelled the way BC's own provider spells it.
    ///
    /// <para><b>Observably equivalent</b> to <c>MetadataDataProvider.CreatePermissionMaskString</c>,
    /// which is what fills these two columns on a service tier: it returns
    /// <c>NavText.Empty</c> for <c>PermissionMask.None</c>, and otherwise one character per set
    /// bit in 0..4, uppercase for the direct bit and lowercase for the indirect bit at n+5. The
    /// loop below is that method's shape, over the same letters read out of its own static
    /// array. What it does NOT reproduce is the ORDER-INDEPENDENT part of BC's implementation —
    /// BC sizes a stack buffer with <c>PopCount((num &gt;&gt; 5) | (num &amp; 0x1F))</c> and
    /// fills it in bit order, so direct and indirect bits for the same permission cannot both
    /// appear; this walks the same bits in the same order and takes the direct bit first, which
    /// is the same choice.</para>
    ///
    /// <para>The attribute BC emits is the mask's NUMERIC value ("16"), and BC's own document
    /// parser reads it with <c>Enum.Parse(typeof(PermissionMask), value)</c>, which accepts
    /// both a numeric string and a member name. Both spellings are accepted here for the same
    /// reason. On a codeunit AL only permits X (Execute, 16) for either property — anything
    /// else is AL0195 — so 16 is the only value observed in practice; the full walk is written
    /// out anyway because the same two columns exist on Table and Page Metadata, where the
    /// mask is not restricted, and a half-implementation here would be the wrong thing to copy.</para>
    /// </summary>
    private static string ReadPermissionMaskString(XElement root, string attributeName, int codeunitId)
    {
        var raw = Attr(root, attributeName);
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        int mask;
        if (!int.TryParse(raw, System.Globalization.NumberStyles.Integer,
                          System.Globalization.CultureInfo.InvariantCulture, out mask))
        {
            if (!TryParsePermissionMaskNames(raw, out mask))
                throw CodeunitMetadataShapeGap(
                    $"codeunit {codeunitId} states {attributeName}='{raw}' in BC's metadata document, "
                    + "which is neither a numeric PermissionMask nor a comma-separated list of its "
                    + "member names — the runner cannot spell a mask it cannot read, and answering "
                    + "an empty string would be indistinguishable from a codeunit declaring none");
        }

        if (mask == 0) return string.Empty;

        var sb = new System.Text.StringBuilder(PermissionMaskLetters.Length);
        for (int bit = 0; bit < PermissionMaskLetters.Length; bit++)
        {
            if ((mask & (1 << bit)) != 0) sb.Append(PermissionMaskLetters[bit]);
            else if ((mask & (1 << (bit + PermissionMaskLetters.Length))) != 0)
                sb.Append(char.ToLowerInvariant(PermissionMaskLetters[bit]));
        }
        return sb.ToString();
    }

    /// <summary>
    /// The member-name spelling of a PermissionMask, e.g. "Execute" or "Read, Insert" — what
    /// <c>Enum.Parse</c> accepts alongside the numeric form BC actually emits. Resolved against
    /// the letters this file already knows rather than against the runtime enum, so a member
    /// BC adds outside the five permission bits (the mask's HasExpDate / IgnoreWildcard flags,
    /// which no permission column spells) cannot silently become a letter.
    /// </summary>
    private static bool TryParsePermissionMaskNames(string raw, out int mask)
    {
        mask = 0;
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var name = part.Trim();
            var bit = name.ToLowerInvariant() switch
            {
                "none" => -1,
                "read" => 0,
                "insert" => 1,
                "modify" => 2,
                "delete" => 3,
                "execute" => 4,
                "indirectread" => 5,
                "indirectinsert" => 6,
                "indirectmodify" => 7,
                "indirectdelete" => 8,
                "indirectexecute" => 9,
                _ => -2,
            };
            if (bit == -2) return false;
            if (bit >= 0) mask |= 1 << bit;
        }
        return true;
    }

    /// <summary>
    /// The RequiredTestIsolation column's ordinal for one codeunit, or -1 when BC's document
    /// states nothing this column can carry.
    ///
    /// <para><b>The attribute name is the compiler's, not BC's parser's, and that is deliberate.</b>
    /// The AL compiler emits <c>TestIsolation="Disabled"</c>; BC's own
    /// <c>Types.Metadata.MetaCodeunit(XmlNode)</c> switches on attribute name and compares
    /// against the string <c>"RequiredTestIsolation"</c>, which the compiler never writes. So
    /// BC's document parse leaves <c>MetaCodeunit.RequiredTestIsolation</c> at its field
    /// initializer, 0 = None, for every codeunit — including one that declares
    /// <c>TestIsolation = Codeunit</c>.</para>
    ///
    /// <para>That is a fact about BC's XML path, which is not the path a service tier's
    /// CodeUnit Metadata row travels: <c>CodeUnitDataProvider</c> reads
    /// <c>NclMetadata.GetMetaCodeunitById(...).RequiredTestIsolation</c>, an
    /// <c>NCLMetaCodeunit</c> built from the published app's compiled attribute rather than
    /// from this document — the same asymmetry <see cref="AlSubtypeTheCompilerDoesNotEmit"/>
    /// records for SubType. Reading the compiler's own attribute is what makes the runner's
    /// answer track the DECLARATION, which is what the tier reports.</para>
    ///
    /// <para>Corpus PR 296 puts that claim in front of eight real service tiers:
    /// <c>Record_CodeunitMetadata_Get_TestRunnerCodeunits_ReportEachDeclaredTestIsolation</c>
    /// asserts a TestRunner declaring each of Disabled / Codeunit / Function reports the
    /// matching member, and
    /// <c>Record_CodeunitMetadata_Get_CodeunitStatingNoTestIsolation_ReportsTheUnstatedValue</c>
    /// asserts that a codeunit stating none reports something DIFFERENT from an explicit
    /// Disabled. If a tier disagrees, the corpus is right and this method is what changes.</para>
    ///
    /// <para><b>The absent case is left to BC's default, never mapped to a member.</b> AL
    /// permits TestIsolation only on Subtype = TestRunner (AL0223), so the compiler omits the
    /// attribute for every ordinary codeunit — and for a Subtype = Test codeunit, which is the
    /// 32-of-1690 group on Base Application. Returning -1 here means the column keeps
    /// <c>NavValue.GetDefaultNavValue</c>, ordinal 0, which is the member the column names
    /// None and the value BC's own field initializer holds. Substituting Disabled instead —
    /// the value BC emits for a plain codeunit's document — would make a declared Disabled and
    /// an unstated property indistinguishable.</para>
    /// </summary>
    private static int ReadRequiredTestIsolationOrdinal(
        XElement root, IReadOnlyDictionary<string, int>? ordinals, int codeunitId)
    {
        var raw = Attr(root, "TestIsolation");
        if (string.IsNullOrWhiteSpace(raw) || ordinals == null) return -1;

        if (ordinals.TryGetValue(NormalizeObjectTypeName(raw), out var ordinal)) return ordinal;

        // A member the column's own option string does not name is refused, not defaulted —
        // the same rule ResolveCodeunitSubtypeOrdinal applies to SubType (#3080). Defaulting
        // would write ordinal 0 (None) for a codeunit that declares an isolation mode, which
        // reads exactly like a codeunit declaring nothing.
        throw CodeunitMetadataShapeGap(
            $"codeunit {codeunitId} states TestIsolation = '{raw}' in BC's metadata document, "
            + "which is not a member of that column's own option set");
    }

    /// <summary>
    /// The RequiredTestIsolation column's own option ordinals, by member name, read off the
    /// live metatable exactly the way <see cref="EnsureCodeunitSubtypeOrdinals"/> reads
    /// SubType's — never a hardcoded table, so the mapping tracks whatever the System package
    /// in the resolved artifact declares.
    /// <para>Deliberately no runtime-enum overlay. The column names four members
    /// (None,Disabled,Codeunit,Function) and <c>Types.TestCodeunitRequiredTestIsolation</c>
    /// carries the same four at the same ordinals, so an overlay would add nothing and would
    /// introduce the failure mode <see cref="EnsureCodeunitSubtypeOrdinals"/> documents for
    /// SubType, where the enum reaches one member further than the column.</para>
    /// <para>Answers null rather than throwing when the column is absent: an artifact whose
    /// CodeUnit Metadata has no such column is one where the value has nowhere to go, and the
    /// other three columns are still answerable. <see cref="BuildCodeunitMetadataValue"/>
    /// never asks for an ordinal it has no column for.</para>
    /// </summary>
    private static Dictionary<string, int>? EnsureCodeunitTestIsolationOrdinals(NCLMetaTable metaTable)
    {
        if (_cmvIsolationOrdinals != null) return _cmvIsolationOrdinals;

        var field = (GetAllFields(metaTable) ?? Enumerable.Empty<NCLMetaField>())
            .FirstOrDefault(f => NormalizeObjectTypeName(f.FieldName ?? string.Empty) == "requiredtestisolation");
        var optionString = field?.FieldOptionMetadata?.OptionString;
        if (string.IsNullOrEmpty(optionString)) return null;

        var map = BuildMetadataOptionOrdinals(optionString, bcRuntimeEnum: null);
        if (map.Count == 0) return null;

        _cmvIsolationOrdinals = map;
        return map;
    }

    private static Dictionary<string, int>? _cmvIsolationOrdinals;
}
