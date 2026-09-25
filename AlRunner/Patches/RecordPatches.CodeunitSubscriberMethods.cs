// RecordPatches.CodeunitSubscriberMethods — the codeunit <Methods> subtree completed from the app's
// own R2R assembly (#3788): subscribers the symbol file cannot state, merged with its publishers in
// [SignatureSpan] source order. Anything not stated exactly withholds the whole codeunit, because
// MetadataObjectDiff pairs Methods and Parameters positionally (loud-failures.md). Harness-only:
// CodeUnit Metadata (2000000137) has no method column.
// See docs/codeunit-metadata-from-bc.md#subscribers-from-the-assembly.

using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection.Metadata;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    /// <summary>What one codeunit type's metadata says, or why it cannot be used.</summary>
    private sealed record CodeunitAssemblyFacts(
        IReadOnlyDictionary<int, long> SpanByMethodId,
        IReadOnlyDictionary<string, List<long>> ScopeSpansByName,
        IReadOnlyList<(long Span, BcAppSymbolCache.CodeunitMethodSymbol Method)> Subscribers,
        IReadOnlySet<int> InherentMethodIds,
        IReadOnlyDictionary<int, (long Span, BcAppSymbolCache.CodeunitMethodSymbol? Method, string Why)> AssemblyInherentMethods,
        IReadOnlyList<string> PublisherNames,
        string? Refusal);

    private static readonly ConcurrentDictionary<string, IReadOnlyDictionary<int, CodeunitAssemblyFacts>?>
        _codeunitAssemblyFacts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The method table for one precompiled codeunit and whether it is COMPLETE: every attributed
    /// method the assembly declares is in it — subscribers and <c>local</c>
    /// <c>InherentPermissions</c> methods derived here, publishers and the other
    /// <c>InherentPermissions</c> methods from the symbol file — and, when any had to be merged
    /// in, every method was placed in source order. Anything else answers the symbol file's list
    /// with <c>false</c>, which keeps the honest absence.
    /// </summary>
    private static (List<BcAppSymbolCache.CodeunitMethodSymbol>? Methods, bool Complete) ResolveCodeunitMethodTable(
        string appPath, int codeunitId, List<BcAppSymbolCache.CodeunitMethodSymbol>? symbolMethods)
    {
        // No R2R code to read at all — an app registered with its witness but without its
        // assemblies on disk. Only the witness can answer then, as it did before this file.
        if (EnsureCodeunitAssemblyFacts(appPath) is null)
            return (symbolMethods, AssemblyProvesNoSubscriber(appPath, codeunitId));
        return TryMergeSubscriberMethods(appPath, codeunitId, symbolMethods, out var merged, out _)
            ? (merged, true)
            : (symbolMethods, false);
    }

    /// <summary>Test seam: why a codeunit's subtree was withheld, or null when it was derived.</summary>
    internal static string? CodeunitMethodTableRefusalForTests(string appPath, int codeunitId,
        List<BcAppSymbolCache.CodeunitMethodSymbol>? symbolMethods)
        => TryMergeSubscriberMethods(appPath, codeunitId, symbolMethods, out _, out var why) ? null : why;

    private static bool TryMergeSubscriberMethods(
        string appPath, int codeunitId, List<BcAppSymbolCache.CodeunitMethodSymbol>? symbolMethods,
        out List<BcAppSymbolCache.CodeunitMethodSymbol>? merged, out string why)
    {
        merged = null;
        var all = EnsureCodeunitAssemblyFacts(appPath);
        if (all is null) { why = "the app's assemblies could not be read"; return false; }
        if (!all.TryGetValue(codeunitId, out var facts)) { why = "no Codeunit type for this id in the app's assemblies"; return false; }
        if (facts.Refusal is not null) { why = facts.Refusal; return false; }

        // An InherentPermissions method the symbol file does not state is a local one (306, 307,
        // 309, 8705); it is read off the assembly, or the codeunit is withheld.
        var symbolIds = (symbolMethods ?? []).Select(m => m.Id).ToHashSet();
        var derivedInherent = new List<(long Span, BcAppSymbolCache.CodeunitMethodSymbol Method)>();
        foreach (var id in facts.InherentMethodIds.Where(id => !symbolIds.Contains(id)))
        {
            if (facts.AssemblyInherentMethods.TryGetValue(id, out var local) && local.Method is not null)
            {
                derivedInherent.Add((local.Span, local.Method));
                continue;
            }
            why = $"InherentPermissions method {id} is not in the symbol file (local) and cannot be derived: "
                  + (local.Why is { Length: > 0 } reason ? reason : "no derivation recorded");
            return false;
        }
        var symbolNames = (symbolMethods ?? []).Select(m => m.Name).ToList();
        foreach (var publisher in facts.PublisherNames)
        {
            if (symbolNames.Remove(publisher)) continue;
            why = $"publisher '{publisher}' is not in the symbol file";
            return false;
        }

        // Nothing derived: the symbol file's own order is BC's (70/70), so nothing needs placing.
        if (facts.Subscribers.Count == 0 && derivedInherent.Count == 0)
        {
            merged = symbolMethods;
            why = "";
            return true;
        }

        var placed = new List<(long Span, BcAppSymbolCache.CodeunitMethodSymbol Method)>(facts.Subscribers);
        placed.AddRange(derivedInherent);
        foreach (var method in symbolMethods ?? [])
        {
            // A method with a body carries [MethodId]; a publisher has none and is found through
            // its generated <Name>_Scope type instead. An overloaded publisher name is ambiguous.
            if (facts.SpanByMethodId.TryGetValue(method.Id, out var span)) { placed.Add((span, method)); continue; }
            if (facts.ScopeSpansByName.TryGetValue(method.Name, out var spans) && spans.Count == 1)
            {
                placed.Add((spans[0], method));
                continue;
            }
            why = $"method {method.Id} '{method.Name}' has no unique source position in the assembly";
            return false;
        }

        if (placed.Select(p => p.Span).Distinct().Count() != placed.Count)
        {
            why = "two methods share a source position";
            return false;
        }

        // SignatureSpan packs the start line into the top 16 bits and the column below it, so the
        // unsigned value orders by source position — BC's document order (70/70, see the doc).
        merged = placed.OrderBy(p => (ulong)p.Span).Select(p => p.Method).ToList();
        why = "";
        return true;
    }

    private static IReadOnlyDictionary<int, CodeunitAssemblyFacts>? EnsureCodeunitAssemblyFacts(string appPath)
    {
        if (string.IsNullOrEmpty(appPath)) return null;
        string path;
        try { path = Path.GetFullPath(appPath); }
        catch { return null; }
        if (_codeunitAssemblyFacts.TryGetValue(path, out var cached)) return cached;
        if (!File.Exists(path)) return null;

        var result = new Dictionary<int, CodeunitAssemblyFacts>();
        try
        {
            foreach (var dllPath in AppLoader.ExtractAllDllPaths(path))
                ScanCodeunitFacts(dllPath, result);
        }
        catch (Exception ex)
        {
            // Loud, and not cached: an unread app has measured nothing (loud-failures.md).
            Console.Error.WriteLine(
                $"[warn] RecordPatches: could not read the R2R assemblies of '{Path.GetFileName(path)}' to "
                + "derive its codeunits' event subscriber methods, so their method tables stay absent: "
                + $"{ex.GetType().Name}: {ex.Message}");
            return null;
        }
        if (result.Count == 0) return null;
        return _codeunitAssemblyFacts.GetOrAdd(path, result);
    }

    private static void ScanCodeunitFacts(string dllPath, Dictionary<int, CodeunitAssemblyFacts> result)
    {
        using var stream = File.OpenRead(dllPath);
        using var pe = new System.Reflection.PortableExecutable.PEReader(stream);
        if (!pe.HasMetadata) return;
        var md = pe.GetMetadataReader();

        foreach (var handle in md.TypeDefinitions)
        {
            var type = md.GetTypeDefinition(handle);
            if (!type.GetDeclaringType().IsNil) continue;
            if (!TryParseAlObjectTypeId(md.GetString(type.Name), CodeunitTypePrefix, out var id)) continue;
            if (result.ContainsKey(id))
            {
                // One codeunit in two chunks: which one BC compiled from is not knowable here.
                result[id] = new CodeunitAssemblyFacts(
                    new Dictionary<int, long>(), new Dictionary<string, List<long>>(), [],
                    new HashSet<int>(), new Dictionary<int, (long, BcAppSymbolCache.CodeunitMethodSymbol?, string)>(),
                    [], "the codeunit type occurs in more than one assembly");
                continue;
            }
            result[id] = ReadCodeunitFacts(md, pe, type);
        }
    }

    private static CodeunitAssemblyFacts ReadCodeunitFacts(MetadataReader md,
        System.Reflection.PortableExecutable.PEReader pe, TypeDefinition type)
    {
        var spanByMethodId = new Dictionary<int, long>();
        var scopeSpans = new Dictionary<string, List<long>>(StringComparer.Ordinal);
        var subscribers = new List<(long, BcAppSymbolCache.CodeunitMethodSymbol)>();
        var inherentMethodIds = new HashSet<int>();
        var assemblyInherent = new Dictionary<int, (long, BcAppSymbolCache.CodeunitMethodSymbol?, string)>();
        var publisherNames = new List<string>();
        string? refusal = null;

        foreach (var nestedHandle in type.GetNestedTypes())
        {
            var nested = md.GetTypeDefinition(nestedHandle);
            string? name = null; long? span = null;
            foreach (var h in nested.GetCustomAttributes())
            {
                var ca = md.GetCustomAttribute(h);
                switch (AttributeTypeName(md, ca))
                {
                    case "NavNameAttribute": name = ca.DecodeValue(AttributeArgumentTypes.Instance).FixedArguments[0].Value as string; break;
                    case "SignatureSpanAttribute": span = ca.DecodeValue(AttributeArgumentTypes.Instance).FixedArguments[0].Value as long?; break;
                }
            }
            if (name is null || span is null) continue;
            if (!scopeSpans.TryGetValue(name, out var list)) scopeSpans[name] = list = [];
            list.Add(span.Value);
        }

        foreach (var methodHandle in type.GetMethods())
        {
            var method = md.GetMethodDefinition(methodHandle);
            int? methodId = null; string? name = null; long? span = null;
            CustomAttributeValue<object>? subscriber = null;
            CustomAttributeValue<object>? inherent = null;
            foreach (var h in method.GetCustomAttributes())
            {
                var ca = md.GetCustomAttribute(h);
                switch (AttributeTypeName(md, ca))
                {
                    case "MethodIdAttribute": methodId = ca.DecodeValue(AttributeArgumentTypes.Instance).FixedArguments[0].Value as int?; break;
                    case "NavNameAttribute": name = ca.DecodeValue(AttributeArgumentTypes.Instance).FixedArguments[0].Value as string; break;
                    case "SignatureSpanAttribute": span = ca.DecodeValue(AttributeArgumentTypes.Instance).FixedArguments[0].Value as long?; break;
                    case "NavEventSubscriberAttribute": subscriber = ca.DecodeValue(AttributeArgumentTypes.Instance); break;
                    case "InherentPermissionsAttribute": inherent = ca.DecodeValue(AttributeArgumentTypes.Instance); break;
                    case "NavEventAttribute": publisherNames.Add(md.GetString(method.Name)); break;
                }
            }
            if (inherent is { } inherentValue)
            {
                if (methodId is { } inheritId)
                {
                    inherentMethodIds.Add(inheritId);
                    // Derived for every one, used only for those the symbol file does not state, so
                    // a public method's shape that cannot be derived here never withholds anything.
                    assemblyInherent[inheritId] = DeriveAssemblyInherentMethod(
                        md, pe, type, method, inheritId, name, span, inherentValue, subscriber is not null);
                }
                else refusal ??= $"InherentPermissions-attributed method '{md.GetString(method.Name)}' carries no MethodId";
            }
            if (methodId is { } mid && span is { } s && !spanByMethodId.TryAdd(mid, s))
                refusal ??= $"method id {mid} occurs twice";

            if (subscriber is null) continue;
            if (methodId is null || name is null || span is null)
            {
                refusal ??= $"subscriber '{md.GetString(method.Name)}' carries no MethodId, NavName or SignatureSpan";
                continue;
            }

            var derived = DeriveSubscriber(md, method, subscriber.Value, out var isTrigger, out var why);
            if (isTrigger) continue;
            if (derived is null) { refusal ??= $"subscriber {methodId} '{name}': {why}"; continue; }
            subscribers.Add((span.Value, new BcAppSymbolCache.CodeunitMethodSymbol(
                methodId.Value, name, "EventSubscriberAttribute", "EventSubscriber",
                Parameters: DeriveAssemblyParameters(md, pe, type, method, out var paramWhy),
                Subscriber: derived)));
            if (subscribers[^1].Item2.Parameters is null) refusal ??= $"subscriber {methodId} '{name}': {paramWhy}";
        }

        return new CodeunitAssemblyFacts(spanByMethodId, scopeSpans, subscribers, inherentMethodIds,
            assemblyInherent, publisherNames, refusal);
    }

    // The two platform codeunits whose "events" are an install or upgrade codeunit's triggers.
    // The compiler marks a trigger such as OnUpgradePerCompany with [NavEventSubscriber] too, but
    // BC's emitter does not write it: 12 of 12 such methods on System Application 28.1.49838.53910
    // are absent from its documents, all PUBLIC, while all 140 emitted subscribers are PRIVATE.
    private const int UpgradeTriggerCodeunitId = 2000000008;
    private const int InstallTriggerCodeunitId = 2000000010;

    // BC's emitter spells SenderType the AL way. Only the kinds observed in BC's documents are
    // listed — a kind not here refuses rather than guessing a spelling (ObjectType names Codeunit
    // "CodeUnit", so the enum name is not the answer).
    private static readonly Dictionary<int, string> SubscriberSenderTypeNames = new()
    {
        [(int)Microsoft.Dynamics.Nav.Types.ObjectType.Table] = "Table",
        [(int)Microsoft.Dynamics.Nav.Types.ObjectType.CodeUnit] = "Codeunit",
        [(int)Microsoft.Dynamics.Nav.Types.ObjectType.Page] = "Page",
    };

    private static BcAppSymbolCache.EventSubscriberSymbol? DeriveSubscriber(
        MetadataReader md, MethodDefinition method, CustomAttributeValue<object> value,
        out bool isTrigger, out string why)
    {
        isTrigger = false;
        var args = value.FixedArguments;
        if (args.Length < 3 || args[0].Value is not int senderType || args[1].Value is not int senderId
            || args[2].Value is not string eventName)
        {
            why = "unrecognised [NavEventSubscriber] constructor";
            return null;
        }

        var isPublic = (method.Attributes & System.Reflection.MethodAttributes.MemberAccessMask)
                       == System.Reflection.MethodAttributes.Public;
        var targetsTrigger = senderType == (int)Microsoft.Dynamics.Nav.Types.ObjectType.CodeUnit && senderId is UpgradeTriggerCodeunitId or InstallTriggerCodeunitId;
        if (isPublic && targetsTrigger) { isTrigger = true; why = ""; return null; }
        if (isPublic || targetsTrigger)
        {
            why = "a subscriber that is only half trigger-shaped (visibility and target disagree)";
            return null;
        }

        // The four constructors after (type, id, event): [memberId,] then field NAME or field ID,
        // then options — see Microsoft.Dynamics.Nav.Types.NavEventSubscriberAttribute.
        var rest = args.Skip(3).Select(a => a.Value).ToList();
        string elementName = ""; int elementId = 0; int options;
        switch (rest)
        {
            case [string n, int o]: elementName = n; options = o; break;
            case [int, string n, int o]: elementName = n; options = o; break;
            case [int f, int o]: elementId = f; options = o; break;
            case [int, int f, int o]: elementId = f; options = o; break;
            default: why = "unrecognised [NavEventSubscriber] constructor"; return null;
        }

        if (!SubscriberSenderTypeNames.TryGetValue(senderType, out var senderTypeName))
        {
            why = $"sender object type {senderType} has no spelling measured against BC's documents";
            return null;
        }

        why = "";
        var (skipLicense, skipPermission) = DecodeSubscriberCallOptions(options);
        return new BcAppSymbolCache.EventSubscriberSymbol(
            senderTypeName, senderId, eventName, elementName, elementId, skipLicense, skipPermission);
    }

    /// <summary>The two flags BC writes, read with BC's own enum so the bit values are not
    /// restated here. Every shipped subscriber sets both or neither, so only a direct test can
    /// tell them apart.</summary>
    internal static (bool SkipOnMissingLicense, bool SkipOnMissingPermission) DecodeSubscriberCallOptions(int options)
    {
        var flags = (Microsoft.Dynamics.Nav.Types.EventSubscriberCallOptions)options;
        return (flags.HasFlag(Microsoft.Dynamics.Nav.Types.EventSubscriberCallOptions.SkipOnMissingLicense),
                flags.HasFlag(Microsoft.Dynamics.Nav.Types.EventSubscriberCallOptions.SkipOnMissingPermission));
    }

    // The RuntimeTypes an assembly-derived parameter may carry and be rendered from the signature
    // alone: those whose C# spelling matched BC's document on every derived method of the measured
    // bundles. NavText, NavCode and NavInterfaceHandle are handled apart, from the method's
    // parameter copy (TransferredParameter); a `var` Text/Code states no length anywhere and refuses.
    private static readonly HashSet<string> SubscriberParameterTypes = new(StringComparer.Ordinal)
    {
        "bool", "int", "Decimal18", "System.Guid", "INavRecordHandle", "NavCodeunitHandle",
        "NavRecordRef", "NavModuleInfo", "NavJsonObject", "NavOption", "NavDate",
    };

    // Generic containers render their arguments without a length (#4084's third decision), so
    // NavText/NavCode are safe INSIDE one.
    private static readonly HashSet<string> SubscriberGenericArgumentTypes = new(StringComparer.Ordinal)
    {
        "NavText", "NavCode", "int", "bool",
    };

    private static List<BcAppSymbolCache.MethodParameterSymbol>? DeriveAssemblyParameters(
        MetadataReader md, System.Reflection.PortableExecutable.PEReader pe, TypeDefinition owner,
        MethodDefinition method, out string why)
    {
        Dictionary<string, ParameterTransfer>? transfers = null;
        why = "";
        var signature = method.DecodeSignature(RuntimeTypeNames.Instance, null);
        var parameters = method.GetParameters().Select(md.GetParameter)
            .Where(p => p.SequenceNumber > 0).OrderBy(p => p.SequenceNumber).ToList();
        if (parameters.Count != signature.ParameterTypes.Length)
        {
            why = "parameter rows and signature disagree";
            return null;
        }

        var result = new List<BcAppSymbolCache.MethodParameterSymbol>();
        for (var i = 0; i < parameters.Count; i++)
        {
            var runtimeType = signature.ParameterTypes[i];
            var byRef = runtimeType.StartsWith("ByRef<", StringComparison.Ordinal);
            var inner = byRef ? runtimeType["ByRef<".Length..^1] : runtimeType;
            var parameterName = md.GetString(parameters[i].Name);
            int? length = null;
            bool? transferIsVar = null;
            if (inner is "NavText" or "NavCode" or "NavInterfaceHandle")
            {
                transfers ??= ReadParameterTransfers(md, pe, owner, method, out why);
                if (transfers is null) return null;
                if (!TransferredParameter(inner, byRef, transfers.GetValueOrDefault(parameterName), out length, out transferIsVar, out why))
                {
                    why = $"parameter '{parameterName}' ({runtimeType}): {why}";
                    return null;
                }
            }
            else if (!IsRenderableSubscriberType(inner))
            {
                why = $"parameter type '{runtimeType}' is not one the derivation can state exactly";
                return null;
            }

            var attributes = new List<string>();
            foreach (var h in parameters[i].GetCustomAttributes())
            {
                var ca = md.GetCustomAttribute(h);
                switch (AttributeTypeName(md, ca))
                {
                    case "NavObjectIdAttribute":
                        var v = ca.DecodeValue(AttributeArgumentTypes.Instance);
                        if (v.FixedArguments.Length != 0 || v.NamedArguments is not [{ Name: "ObjectId", Value: int objectId }])
                        {
                            why = "a [NavObjectId] argument shape not seen in BC's documents";
                            return null;
                        }
                        attributes.Add($"[NavObjectId(ObjectId={objectId})]");
                        break;
                    case "NavByReferenceAttribute":
                        attributes.Add("[NavByReferenceAttribute]");
                        break;
                    default:
                        why = $"parameter attribute '{AttributeTypeName(md, ca)}' has no measured spelling";
                        return null;
                }
            }

            var isVar = transferIsVar ?? (byRef || attributes.Contains("[NavByReferenceAttribute]"));
            result.Add(new BcAppSymbolCache.MethodParameterSymbol(
                parameterName, runtimeType, string.Join(",", attributes), isVar, length));
        }

        why = "";
        return result;
    }

    /// <summary>
    /// A Text/Code or Interface parameter, stated from how the method copies it into its scope
    /// (#4601): a by-value Text/Code through <c>ModifyLength(N)</c> — BC writes <c>Length="N"</c>,
    /// and none for 0, the unbounded form (1482's <c>Text[240]</c> beside a <c>Text</c>) — and an
    /// Interface through <c>ALByValue</c> when by value, as-is when <c>var</c>, which BC writes as
    /// <c>IsVar="True"</c> on a plain <c>NavInterfaceHandle</c> (3920, 8903). A <c>var</c>
    /// Text/Code is copied as-is whatever its declared length, so nothing states the
    /// <c>Length</c> BC writes for one (55's <c>Text[1024]</c>) and it refuses. Observably
    /// equivalent on every derived parameter of the ground-truth bundles:
    /// <c>CodeunitSubscriberMethodTableTests</c>, docs/codeunit-metadata-from-bc.md#what-the-method-body-states.
    /// </summary>
    internal static bool TransferredParameter(string inner, bool byRef, ParameterTransfer? transfer,
        out int? length, out bool? isVar, out string why)
    {
        length = null;
        isVar = null;
        why = "";
        switch (inner, byRef, transfer?.Kind)
        {
            case ("NavText" or "NavCode", true, _):
                why = "a var Text/Code is copied without its declared length, so its Length cannot be stated";
                return false;
            case ("NavText" or "NavCode", false, ParameterTransferKind.ModifyLength) when transfer!.Value.Length >= 0:
                length = transfer.Value.Length == 0 ? null : transfer.Value.Length;
                return true;
            case ("NavInterfaceHandle", false, ParameterTransferKind.ByValue):
                isVar = false;
                return true;
            case ("NavInterfaceHandle", false, ParameterTransferKind.Direct):
                isVar = true;
                return true;
            default:
                why = transfer is null
                    ? "the method never copies it into its scope in a shape the reader knows"
                    : $"copied by {transfer.Value.Kind}, which does not state it for this type";
                return false;
        }
    }

    // The two InherentPermissions values the attribute's uint[] {type, id, mask, scope} carries and
    // BC's documents have been measured against: type 0 is written TableData (all 24 shipped
    // elements), scope 0 is written 0. Any other value refuses rather than guessing a spelling.
    private static (long, BcAppSymbolCache.CodeunitMethodSymbol?, string) DeriveAssemblyInherentMethod(
        MetadataReader md, System.Reflection.PortableExecutable.PEReader pe, TypeDefinition owner,
        MethodDefinition method, int methodId, string? name, long? span,
        CustomAttributeValue<object> value, bool isSubscriber)
    {
        if (isSubscriber) return (0, null, "the method is also a subscriber; which element BC writes for both is not measured");
        if (name is null || span is null) return (0, null, "the method carries no NavName or SignatureSpan");
        var inherent = DecodeAssemblyInherentPermission(value, out var why);
        if (inherent is null) return (0, null, why);
        var parameters = DeriveAssemblyParameters(md, pe, owner, method, out why);
        if (parameters is null) return (0, null, why);
        return (span.Value, new BcAppSymbolCache.CodeunitMethodSymbol(
            methodId, name, "InherentPermissionsMethodAttribute", "InherentPermissions",
            InherentPermission: inherent, Parameters: parameters), "");
    }

    internal static BcAppSymbolCache.InherentPermissionSymbol? DecodeAssemblyInherentPermission(
        CustomAttributeValue<object> value, out string why)
    {
        if (value.FixedArguments is not [{ Value: System.Collections.Immutable.ImmutableArray<CustomAttributeTypedArgument<object>> items }]
            || items.Length != 4 || items.Any(a => a.Value is not uint))
        {
            why = "an [InherentPermissions] argument shape other than four uints";
            return null;
        }
        var (type, id, mask, scope) = ((uint)items[0].Value!, (uint)items[1].Value!, (uint)items[2].Value!, (uint)items[3].Value!);
        if (type != 0) { why = $"object type {type} has no spelling measured against BC's documents"; return null; }
        if (scope != 0) { why = $"scope {scope} has not been measured against BC's documents"; return null; }
        if (mask == 0 || mask > int.MaxValue) { why = $"permission mask {mask} is not one BC writes"; return null; }
        why = "";
        return new BcAppSymbolCache.InherentPermissionSymbol(
            "TableData", id.ToString(System.Globalization.CultureInfo.InvariantCulture), (int)mask, 0);
    }

    private static bool IsRenderableSubscriberType(string type)
    {
        if (SubscriberParameterTypes.Contains(type)) return true;
        foreach (var container in new[] { "NavList<", "NavDictionary<" })
        {
            if (!type.StartsWith(container, StringComparison.Ordinal) || !type.EndsWith('>')) continue;
            var arguments = type[container.Length..^1].Split(',');
            return arguments.All(SubscriberGenericArgumentTypes.Contains);
        }
        return false;
    }

    /// <summary>Custom-attribute argument decoding with every enum read as its Int32 value —
    /// true of <c>ObjectType</c> and <c>EventSubscriberCallOptions</c>, the only two here.</summary>
    private sealed class AttributeArgumentTypes : ICustomAttributeTypeProvider<object>
    {
        internal static readonly AttributeArgumentTypes Instance = new();
        public object GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode;
        public object GetSystemType() => "System.Type";
        public object GetSZArrayType(object elementType) => "[]";
        public object GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => "enum";
        public object GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => "enum";
        public object GetTypeFromSerializedName(string name) => name;
        public PrimitiveTypeCode GetUnderlyingEnumType(object type) => PrimitiveTypeCode.Int32;
        public bool IsSystemType(object type) => type is "System.Type";
    }

    /// <summary>A parameter type spelled the way BC's emitter writes <c>RuntimeType</c>: C#
    /// keywords for primitives, <c>System.*</c> types namespace-qualified, the runtime's own types
    /// by simple name, generics as <c>Name&lt;A,B&gt;</c> with no space.</summary>
    private sealed class RuntimeTypeNames : ISignatureTypeProvider<string, object?>
    {
        internal static readonly RuntimeTypeNames Instance = new();

        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode switch
        {
            PrimitiveTypeCode.Boolean => "bool",
            PrimitiveTypeCode.Int32 => "int",
            PrimitiveTypeCode.Int64 => "long",
            PrimitiveTypeCode.String => "string",
            PrimitiveTypeCode.Void => "void",
            _ => "?" + typeCode,
        };

        private static string Simple(string name) => name.IndexOf('`') is var tick and >= 0 ? name[..tick] : name;

        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
            => Simple(reader.GetString(reader.GetTypeDefinition(handle).Name));

        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
        {
            var reference = reader.GetTypeReference(handle);
            var ns = reader.GetString(reference.Namespace);
            var name = Simple(reader.GetString(reference.Name));
            return ns == "System" || ns.StartsWith("System.", StringComparison.Ordinal) ? ns + "." + name : name;
        }

        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments)
            => genericType + "<" + string.Join(",", typeArguments) + ">";

        public string GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
            => reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

        // Shapes no AL parameter compiles to; spelled so they can never match an allowed type.
        public string GetSZArrayType(string elementType) => elementType + "[]";
        public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[,]";
        public string GetByReferenceType(string elementType) => "ref " + elementType;
        public string GetPointerType(string elementType) => elementType + "*";
        public string GetPinnedType(string elementType) => elementType;
        public string GetFunctionPointerType(MethodSignature<string> signature) => "fnptr";
        public string GetGenericMethodParameter(object? genericContext, int index) => "!!" + index;
        public string GetGenericTypeParameter(object? genericContext, int index) => "!" + index;
        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
    }

    /// <summary>Test seam: forget every derived subscriber table.</summary>
    internal static void ClearCodeunitAssemblyFactsForTests() => _codeunitAssemblyFacts.Clear();
}
