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
        IReadOnlyList<string> PublisherNames,
        string? Refusal);

    private static readonly ConcurrentDictionary<string, IReadOnlyDictionary<int, CodeunitAssemblyFacts>?>
        _codeunitAssemblyFacts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The method table for one precompiled codeunit and whether it is COMPLETE: every attributed
    /// method the assembly declares is in it — subscribers derived here, publishers and
    /// <c>InherentPermissions</c> methods from the symbol file — and, when subscribers had to be
    /// merged in, every method was placed in source order. Anything else answers the symbol
    /// file's list with <c>false</c>, which keeps the honest absence.
    /// </summary>
    /// <remarks>The completeness check applies to a subscriber-free codeunit too: a
    /// <c>local</c> method carrying <c>[InherentPermissions]</c> is emitted by BC and stated by
    /// no symbol file (Business Foundation 306/307/309, System Application 8705), so "no
    /// subscriber" alone never proved the symbol file's list complete.</remarks>
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

        var symbolIds = (symbolMethods ?? []).Select(m => m.Id).ToHashSet();
        var missingInherent = facts.InherentMethodIds.Where(id => !symbolIds.Contains(id)).ToList();
        if (missingInherent.Count > 0)
        {
            why = $"InherentPermissions-attributed method(s) {string.Join(", ", missingInherent)} are not in the symbol file (local)";
            return false;
        }
        var symbolNames = (symbolMethods ?? []).Select(m => m.Name).ToList();
        foreach (var publisher in facts.PublisherNames)
        {
            if (symbolNames.Remove(publisher)) continue;
            why = $"publisher '{publisher}' is not in the symbol file";
            return false;
        }

        // No subscriber: the symbol file's own order is BC's (70/70), so nothing needs placing.
        if (facts.Subscribers.Count == 0)
        {
            merged = symbolMethods;
            why = "";
            return true;
        }

        var placed = new List<(long Span, BcAppSymbolCache.CodeunitMethodSymbol Method)>(facts.Subscribers);
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
                    new HashSet<int>(), [], "the codeunit type occurs in more than one assembly");
                continue;
            }
            result[id] = ReadCodeunitFacts(md, type);
        }
    }

    private static CodeunitAssemblyFacts ReadCodeunitFacts(MetadataReader md, TypeDefinition type)
    {
        var spanByMethodId = new Dictionary<int, long>();
        var scopeSpans = new Dictionary<string, List<long>>(StringComparer.Ordinal);
        var subscribers = new List<(long, BcAppSymbolCache.CodeunitMethodSymbol)>();
        var inherentMethodIds = new HashSet<int>();
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
            bool inherent = false;
            foreach (var h in method.GetCustomAttributes())
            {
                var ca = md.GetCustomAttribute(h);
                switch (AttributeTypeName(md, ca))
                {
                    case "MethodIdAttribute": methodId = ca.DecodeValue(AttributeArgumentTypes.Instance).FixedArguments[0].Value as int?; break;
                    case "NavNameAttribute": name = ca.DecodeValue(AttributeArgumentTypes.Instance).FixedArguments[0].Value as string; break;
                    case "SignatureSpanAttribute": span = ca.DecodeValue(AttributeArgumentTypes.Instance).FixedArguments[0].Value as long?; break;
                    case "NavEventSubscriberAttribute": subscriber = ca.DecodeValue(AttributeArgumentTypes.Instance); break;
                    case "InherentPermissionsAttribute": inherent = true; break;
                    case "NavEventAttribute": publisherNames.Add(md.GetString(method.Name)); break;
                }
            }
            if (inherent)
            {
                if (methodId is { } inheritId) inherentMethodIds.Add(inheritId);
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
                Parameters: DeriveSubscriberParameters(md, method, out var paramWhy),
                Subscriber: derived)));
            if (subscribers[^1].Item2.Parameters is null) refusal ??= $"subscriber {methodId} '{name}': {paramWhy}";
        }

        return new CodeunitAssemblyFacts(spanByMethodId, scopeSpans, subscribers, inherentMethodIds, publisherNames, refusal);
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

    // The RuntimeTypes a subscriber parameter may carry and be rendered: those whose C# spelling
    // matched BC's document on every subscriber of the measured bundles. NavText and NavCode are
    // absent deliberately: BC writes Length="N" for a declared length, and the signature does not
    // carry it (it lives in the body as ModifyLength(N)), so it cannot be stated. NavInterfaceHandle
    // is absent because BC writes IsVar="True" for a `var` interface with nothing in the signature
    // saying so.
    private static readonly HashSet<string> SubscriberParameterTypes = new(StringComparer.Ordinal)
    {
        "bool", "int", "Decimal18", "System.Guid", "INavRecordHandle", "NavCodeunitHandle",
        "NavRecordRef", "NavModuleInfo", "NavJsonObject", "NavOption",
    };

    // Generic containers render their arguments without a length (#4084's third decision), so
    // NavText/NavCode are safe INSIDE one.
    private static readonly HashSet<string> SubscriberGenericArgumentTypes = new(StringComparer.Ordinal)
    {
        "NavText", "NavCode", "int", "bool",
    };

    private static List<BcAppSymbolCache.MethodParameterSymbol>? DeriveSubscriberParameters(
        MetadataReader md, MethodDefinition method, out string why)
    {
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
            if (!IsRenderableSubscriberType(inner))
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

            var isVar = byRef || attributes.Contains("[NavByReferenceAttribute]");
            result.Add(new BcAppSymbolCache.MethodParameterSymbol(
                md.GetString(parameters[i].Name), runtimeType, string.Join(",", attributes), isVar, null));
        }

        why = "";
        return result;
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
