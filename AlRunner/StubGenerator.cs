using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Dynamics.Nav.CodeAnalysis;

namespace AlRunner;

/// <summary>
/// Generates empty AL stub files from .app symbol packages.
/// Reads SymbolReference.json from each .app file and emits one .al file per codeunit.
/// When a <see cref="Compilation"/> is provided, also generates stubs for platform/system
/// codeunits that are not listed in SymbolReference.json.
/// </summary>
public static class StubGenerator
{
    /// <summary>Codeunit IDs that al-runner already mocks natively — skip these.</summary>
    private static readonly HashSet<int> SkipIds = new()
    {
        130,     // Assert (LibraryAssert.al)
        131,     // Library Assert alias (Assert.al)
        130000,  // Assert BaseApp (routed to MockAssert)
        130002,  // Library Assert real BC ID (routed to MockAssert)
        130440,  // Library - Random (LibraryRandom.al)
        130500,  // Any (LibraryAny.al)
        131003,  // Library - Utility (LibraryUtility.al)
        131004,  // Library - Variable Storage (LibraryVariableStorage.al)
        131100,  // AL Runner Config
        132250,  // Library - Test Initialize (LibraryTestInitialize.al)
    };

    public record GenerateResult(
        int Generated,
        List<string> SkippedExisting,
        int SkippedNonCodeunit,
        int SkippedNativeMock,
        int SkippedNotReferenced,
        int TotalAvailable,
        int SourceFileCount,
        int GeneratedFromSymbolTable = 0);

    /// <summary>
    /// Scan all .app files in <paramref name="packagesDir"/>, extract codeunit symbols,
    /// and write one .al stub file per codeunit into <paramref name="outputDir"/>.
    /// When <paramref name="sourceDirs"/> is provided and non-empty, only codeunits
    /// referenced in the AL source are generated.
    /// </summary>
    public static GenerateResult Generate(string packagesDir, string outputDir, IReadOnlyList<string>? sourceDirs = null)
        => Generate(new[] { packagesDir }, outputDir, sourceDirs, compilation: null);

    /// <summary>
    /// Scan all .app files in each of <paramref name="packagesDirs"/>, extract codeunit symbols,
    /// and write one .al stub file per codeunit into <paramref name="outputDir"/>.
    /// When <paramref name="sourceDirs"/> is provided and non-empty, only codeunits
    /// referenced in the AL source are generated.
    /// </summary>
    public static GenerateResult Generate(IReadOnlyList<string> packagesDirs, string outputDir, IReadOnlyList<string>? sourceDirs = null)
        => Generate(packagesDirs, outputDir, sourceDirs, compilation: null);

    /// <summary>
    /// Scan all .app files in each of <paramref name="packagesDirs"/>, extract codeunit symbols,
    /// and write one .al stub file per codeunit into <paramref name="outputDir"/>.
    /// When <paramref name="sourceDirs"/> is provided and non-empty, only codeunits
    /// referenced in the AL source are generated.
    /// When <paramref name="compilation"/> is provided, also generates stubs for platform/system
    /// codeunits that have no SymbolReference.json in their .app package (e.g. Rest Client,
    /// No. Series Batch, etc.) by querying the BC compiler's symbol table directly.
    /// </summary>
    public static GenerateResult Generate(IReadOnlyList<string> packagesDirs, string outputDir,
        IReadOnlyList<string>? sourceDirs, Compilation? compilation)
    {
        foreach (var packagesDir in packagesDirs)
        {
            if (!Directory.Exists(packagesDir))
                throw new DirectoryNotFoundException($"Packages directory not found: {packagesDir}");
        }

        Directory.CreateDirectory(outputDir);

        // Collect source text for filtering (if source dirs provided)
        var (sourceTexts, sourceFileCount) = CollectSourceTexts(sourceDirs);
        bool filtering = sourceTexts != null;

        int generated = 0;
        int skippedNonCodeunit = 0;
        int skippedNativeMock = 0;
        int skippedNotReferenced = 0;
        int totalAvailable = 0;
        int generatedFromSymbolTable = 0;
        var skippedExisting = new List<string>();
        var writtenIds = new HashSet<int>();

        // Pass 1: SymbolReference.json inside .app packages
        foreach (var packagesDir in packagesDirs)
        {
            var appFiles = Directory.GetFiles(packagesDir, "*.app", SearchOption.TopDirectoryOnly);

            foreach (var appFile in appFiles)
            {
                var (codeunits, nonCodeunitCount, appName) = ReadCodeunitsFromApp(appFile);
                skippedNonCodeunit += nonCodeunitCount;
                totalAvailable += codeunits.Count;

                foreach (var cu in codeunits)
                {
                    if (SkipIds.Contains(cu.Id))
                    {
                        skippedNativeMock++;
                        continue;
                    }

                    if (filtering && !IsCodeunitReferenced(cu, sourceTexts!))
                    {
                        skippedNotReferenced++;
                        continue;
                    }

                    // Filter procedures when source dirs are provided
                    var filteredCu = filtering ? FilterProcedures(cu, sourceTexts!) : cu;

                    var fileName = $"Cod{cu.Id}.{SanitizeFileName(cu.Name)}.al";
                    var filePath = Path.Combine(outputDir, fileName);

                    writtenIds.Add(cu.Id);

                    if (File.Exists(filePath))
                    {
                        skippedExisting.Add(fileName);
                        continue;
                    }

                    var content = RenderCodeunit(filteredCu, appName);
                    File.WriteAllText(filePath, content, new UTF8Encoding(false));
                    generated++;
                }
            }
        }

        // Pass 2: Symbol-table pass for platform/system codeunits missing from SymbolReference.json
        if (compilation != null)
        {
            var symbolTableCus = GetAllCodeunitsFromSymbolTable(compilation);
            foreach (var (cuId, cuName, cuSymbol) in symbolTableCus)
            {
                if (writtenIds.Contains(cuId))
                    continue;
                if (SkipIds.Contains(cuId))
                    continue;

                // Apply source filtering if requested
                if (filtering)
                {
                    var tempCu = new CodeunitSymbol(cuId, cuName, new List<MethodSymbol>());
                    if (!IsCodeunitReferenced(tempCu, sourceTexts!))
                        continue;
                }

                var fileName = $"Cod{cuId}.{SanitizeFileName(cuName)}.al";
                var filePath = Path.Combine(outputDir, fileName);
                writtenIds.Add(cuId);

                if (File.Exists(filePath))
                {
                    skippedExisting.Add(fileName);
                    continue;
                }

                var content = RenderCodeunitFromSymbol(cuId, cuName, cuSymbol, filtering ? sourceTexts : null);
                File.WriteAllText(filePath, content, new UTF8Encoding(false));
                generated++;
                generatedFromSymbolTable++;
            }
        }

        return new GenerateResult(generated, skippedExisting, skippedNonCodeunit,
            skippedNativeMock, skippedNotReferenced, totalAvailable, sourceFileCount, generatedFromSymbolTable);
    }

    /// <summary>
    /// Read all .al files from the given source directories and return their contents
    /// as a single concatenated lowercase string for matching, plus the file count.
    /// Returns (null, 0) when no source dirs are provided.
    /// </summary>
    public static (string? CombinedText, int FileCount) CollectSourceTexts(IReadOnlyList<string>? sourceDirs)
    {
        if (sourceDirs == null || sourceDirs.Count == 0)
            return (null, 0);

        var sb = new StringBuilder();
        int fileCount = 0;

        foreach (var dir in sourceDirs)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var alFile in Directory.GetFiles(dir, "*.al", SearchOption.AllDirectories))
            {
                sb.AppendLine(File.ReadAllText(alFile));
                fileCount++;
            }
        }

        return (sb.ToString(), fileCount);
    }

    // Matches Codeunit::"Some Name" or Codeunit::SingleWord in AL source.
    // Group 1 captures the quoted name; Group 2 captures an unquoted identifier (letters, digits, underscores, hyphens only).
    // Used to detect EventSubscriber attributes and other Codeunit:: references.
    private static readonly Regex CodeunitDoubleColonPattern =
        new(@"Codeunit::(?:""([^""]*)""|(\w[\w\-]*))", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Check if a codeunit is referenced in the source text.
    /// Matches by:
    /// - Codeunit ID numeric literal anywhere in source
    /// - Codeunit name (case-insensitive) anywhere in source
    /// - <c>Codeunit::"Name"</c> pattern (EventSubscriber, Codeunit.Run, etc.)
    /// - <c>Codeunit::Name</c> pattern (single-word names without quotes)
    /// </summary>
    public static bool IsCodeunitReferenced(CodeunitSymbol cu, string sourceText)
    {
        // Check by codeunit ID — the numeric literal appearing anywhere in source
        if (sourceText.Contains(cu.Id.ToString(), StringComparison.Ordinal))
            return true;

        // Check by name (case-insensitive) — covers variable declarations, string literals, etc.
        if (sourceText.Contains(cu.Name, StringComparison.OrdinalIgnoreCase))
            return true;

        // Check Codeunit::"Name" and Codeunit::Name patterns (EventSubscriber attributes,
        // Codeunit.Run calls, etc.) — catches references not via variable declarations.
        foreach (Match m in CodeunitDoubleColonPattern.Matches(sourceText))
        {
            // Group 1: quoted name; Group 2: unquoted single-word name
            var refName = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value.Trim();
            if (string.Equals(refName, cu.Name, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Return a copy of the codeunit with only procedures that appear to be called
    /// in the source text. A procedure is considered called if its name followed by
    /// '(' appears in the source (case-insensitive).
    /// </summary>
    public static CodeunitSymbol FilterProcedures(CodeunitSymbol cu, string sourceText)
    {
        var filtered = cu.Methods.Where(m =>
            sourceText.Contains(m.Name + "(", StringComparison.OrdinalIgnoreCase)
            || sourceText.Contains(m.Name + " (", StringComparison.OrdinalIgnoreCase))
            .ToList();

        return cu with { Methods = filtered };
    }

    // --- Internal types for parsed symbol data ---

    public record CodeunitSymbol(int Id, string Name, List<MethodSymbol> Methods);
    public record MethodSymbol(string Name, List<ParameterSymbol> Parameters, TypeSymbol? ReturnType);
    public record ParameterSymbol(string Name, TypeSymbol Type, bool IsVar);
    public record TypeSymbol(string Name, string? Subtype, bool Temporary, List<TypeSymbol>? TypeArguments);

    // --- Parsing ---

    public static (List<CodeunitSymbol> Codeunits, int NonCodeunitCount, string AppName) ReadCodeunitsFromApp(string appPath)
    {
        var fileBytes = File.ReadAllBytes(appPath);
        var (data, zipOffset) = ReadAppFileHeader(fileBytes);

        using var zipStream = new MemoryStream(data, zipOffset, data.Length - zipOffset);
        using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read);

        var symbolEntry = zip.GetEntry("SymbolReference.json");
        if (symbolEntry == null)
        {
            // Try nested .app (Ready2Run package)
            var nestedApp = zip.Entries.FirstOrDefault(e =>
                e.FullName.EndsWith(".app", StringComparison.OrdinalIgnoreCase) && !e.FullName.Contains('/'));
            if (nestedApp != null)
            {
                using var nestedStream = nestedApp.Open();
                using var ms = new MemoryStream();
                nestedStream.CopyTo(ms);
                var nestedBytes = ms.ToArray();
                var (nestedData, nestedOffset) = ReadAppFileHeader(nestedBytes);

                using var nestedZipStream = new MemoryStream(nestedData, nestedOffset, nestedData.Length - nestedOffset);
                using var nestedZip = new ZipArchive(nestedZipStream, ZipArchiveMode.Read);
                symbolEntry = nestedZip.GetEntry("SymbolReference.json");
                if (symbolEntry != null)
                    return ParseSymbolReference(nestedZip, appPath);
            }
            return (new List<CodeunitSymbol>(), 0, Path.GetFileName(appPath));
        }

        return ParseSymbolReference(zip, appPath);
    }

    private static (List<CodeunitSymbol>, int NonCodeunitCount, string AppName) ParseSymbolReference(
        ZipArchive zip, string appPath)
    {
        var symbolEntry = zip.GetEntry("SymbolReference.json");
        if (symbolEntry == null)
            return (new List<CodeunitSymbol>(), 0, Path.GetFileName(appPath));

        using var stream = symbolEntry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var json = reader.ReadToEnd();

        // Strip UTF-8 BOM if present
        if (json.Length > 0 && json[0] == '\uFEFF')
            json = json[1..];

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var appName = Path.GetFileName(appPath);
        if (root.TryGetProperty("Name", out var nameEl))
            appName = nameEl.GetString() ?? appName;

        // Count non-codeunit objects
        int nonCodeunitCount = 0;
        foreach (var prop in new[] { "Tables", "Pages", "Reports", "XmlPorts", "Queries",
            "ControlAddIns", "EnumTypes", "Interfaces", "PermissionSets",
            "PermissionSetExtensions", "ReportExtensions" })
        {
            if (root.TryGetProperty(prop, out var arr) && arr.ValueKind == JsonValueKind.Array)
                nonCodeunitCount += arr.GetArrayLength();
        }

        var codeunits = new List<CodeunitSymbol>();
        if (!root.TryGetProperty("Codeunits", out var cuArray) || cuArray.ValueKind != JsonValueKind.Array)
            return (codeunits, nonCodeunitCount, appName);

        foreach (var cuEl in cuArray.EnumerateArray())
        {
            var id = cuEl.GetProperty("Id").GetInt32();
            var name = cuEl.GetProperty("Name").GetString() ?? "";

            var methods = new List<MethodSymbol>();
            if (cuEl.TryGetProperty("Methods", out var methodsEl))
            {
                foreach (var mEl in methodsEl.EnumerateArray())
                {
                    // Skip internal/local methods — only emit exported (public) ones.
                    // In SymbolReference.json, all listed methods are exported by definition,
                    // but skip any with EventSubscriber/IntegrationEvent/BusinessEvent attributes
                    // as those are event wiring, not callable procedures.
                    if (HasEventAttribute(mEl))
                        continue;

                    var mName = mEl.GetProperty("Name").GetString() ?? "";
                    var parameters = new List<ParameterSymbol>();

                    if (mEl.TryGetProperty("Parameters", out var paramsEl))
                    {
                        foreach (var pEl in paramsEl.EnumerateArray())
                        {
                            var pName = pEl.GetProperty("Name").GetString() ?? "";
                            var isVar = pEl.TryGetProperty("IsVar", out var isVarEl) && isVarEl.GetBoolean();
                            var pType = ParseTypeDefinition(pEl.GetProperty("TypeDefinition"));
                            parameters.Add(new ParameterSymbol(pName, pType, isVar));
                        }
                    }

                    TypeSymbol? returnType = null;
                    if (mEl.TryGetProperty("ReturnTypeDefinition", out var rtEl) && rtEl.ValueKind == JsonValueKind.Object)
                    {
                        returnType = ParseTypeDefinition(rtEl);
                    }

                    methods.Add(new MethodSymbol(mName, parameters, returnType));
                }
            }

            codeunits.Add(new CodeunitSymbol(id, name, methods));
        }

        return (codeunits, nonCodeunitCount, appName);
    }

    private static bool HasEventAttribute(JsonElement methodEl)
    {
        if (!methodEl.TryGetProperty("Attributes", out var attrs) || attrs.ValueKind != JsonValueKind.Array)
            return false;
        foreach (var attr in attrs.EnumerateArray())
        {
            var name = attr.TryGetProperty("Name", out var n) ? n.GetString() ?? "" : "";
            if (name is "EventSubscriber" or "IntegrationEvent" or "BusinessEvent")
                return true;
        }
        return false;
    }

    private static TypeSymbol ParseTypeDefinition(JsonElement el)
    {
        var name = el.GetProperty("Name").GetString() ?? "";
        string? subtype = null;
        bool temporary = false;
        List<TypeSymbol>? typeArgs = null;

        if (el.TryGetProperty("Subtype", out var stEl) && stEl.ValueKind == JsonValueKind.Object)
        {
            subtype = stEl.TryGetProperty("Name", out var stName) ? stName.GetString() : null;
        }

        if (el.TryGetProperty("Temporary", out var tempEl) && tempEl.GetBoolean())
            temporary = true;

        if (el.TryGetProperty("TypeArguments", out var taEl) && taEl.ValueKind == JsonValueKind.Array)
        {
            typeArgs = new List<TypeSymbol>();
            foreach (var ta in taEl.EnumerateArray())
                typeArgs.Add(ParseTypeDefinition(ta));
        }

        return new TypeSymbol(name, subtype, temporary, typeArgs);
    }

    // --- Rendering ---

    internal static string RenderCodeunit(CodeunitSymbol cu, string appName)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"// Auto-generated stub from {appName} — fill in implementations as needed.");
        sb.AppendLine($"codeunit {cu.Id} \"{EscapeAlString(cu.Name)}\"");
        sb.AppendLine("{");

        foreach (var method in cu.Methods)
        {
            sb.Append($"    procedure {method.Name}(");
            sb.Append(string.Join("; ", method.Parameters.Select(RenderParameter)));
            sb.Append(')');

            if (method.ReturnType != null)
                sb.Append($": {RenderType(method.ReturnType)}");

            sb.AppendLine();
            sb.AppendLine("    begin");

            if (method.ReturnType != null)
                sb.AppendLine($"        exit({DefaultValueForType(method.ReturnType)}); // TODO: implement");

            sb.AppendLine("    end;");
            sb.AppendLine();
        }

        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string RenderParameter(ParameterSymbol p)
    {
        var prefix = p.IsVar ? "var " : "";
        return $"{prefix}{p.Name}: {RenderType(p.Type)}";
    }

    public static string RenderType(TypeSymbol t)
    {
        // Simple types: Boolean, Integer, Decimal, Date, Time, DateTime, Duration, Guid, etc.
        // Types with length: Text[100], Code[20] — these come as Name directly from the JSON
        // Compound types: Record "X", Codeunit "X", Page "X", Enum "X", Interface "X"

        switch (t.Name)
        {
            case "Record":
                var temp = t.Temporary ? " temporary" : "";
                return $"Record \"{EscapeAlString(t.Subtype ?? "Unknown")}\"{temp}";
            case "Codeunit":
                return $"Codeunit \"{EscapeAlString(t.Subtype ?? "Unknown")}\"";
            case "Page":
                return $"Page \"{EscapeAlString(t.Subtype ?? "Unknown")}\"";
            case "TestPage":
                return $"TestPage \"{EscapeAlString(t.Subtype ?? "Unknown")}\"";
            case "TestRequestPage":
                return $"TestRequestPage \"{EscapeAlString(t.Subtype ?? "Unknown")}\"";
            case "Enum":
                return $"Enum \"{EscapeAlString(t.Subtype ?? "Unknown")}\"";
            case "Interface":
                return $"Interface \"{EscapeAlString(t.Subtype ?? "Unknown")}\"";
            case "List":
                if (t.TypeArguments?.Count > 0)
                    return $"List of [{RenderType(t.TypeArguments[0])}]";
                return "List of [Text]";
            case "Dictionary":
                if (t.TypeArguments?.Count >= 2)
                    return $"Dictionary of [{RenderType(t.TypeArguments[0])}, {RenderType(t.TypeArguments[1])}]";
                return "Dictionary of [Text, Text]";
            case "RecordRef":
                return "RecordRef";
            case "RecordId":
                return "RecordId";
            case "Variant":
                return "Variant";
            case "BigText":
                return "BigText";
            case "InStream":
                return "InStream";
            case "OutStream":
                return "OutStream";
            case "DateFormula":
                return "DateFormula";
            case "XmlDocument":
                return "XmlDocument";
            case "DotNet":
                return $"DotNet \"{EscapeAlString(t.Subtype ?? "Unknown")}\"";
            case "Option":
                return "Option";
            case "ObjectType":
                return "ObjectType";
            case "TextEncoding":
                return "TextEncoding";
            case "TestPermissions":
                return "TestPermissions";
            default:
                // Text, Code[20], Text[100], Boolean, Integer, Decimal, Date, Time, DateTime, Duration, Guid, etc.
                return t.Name;
        }
    }

    public static string DefaultValueForType(TypeSymbol t)
    {
        return t.Name switch
        {
            "Boolean" => "false",
            "Integer" => "0",
            "Decimal" => "0",
            "Text" or "Code[20]" or "Code[10]" or "Code[50]" => "''",
            "Date" => "0D",
            "Time" => "0T",
            "DateTime" => "0DT",
            "Guid" => "'{00000000-0000-0000-0000-000000000000}'",
            "Duration" => "0",
            "Option" or "ObjectType" or "TextEncoding" or "TestPermissions" => "0",
            _ when t.Name.StartsWith("Text[") => "''",
            _ when t.Name.StartsWith("Code[") => "''",
            _ when t.Name is "Enum" => "0",
            _ when t.Name is "Record" or "Codeunit" or "Page" or "TestPage" or "TestRequestPage"
                or "Interface" or "Variant" or "RecordRef" or "RecordId" or "BigText"
                or "InStream" or "OutStream" or "DateFormula" or "XmlDocument"
                or "List" or "Dictionary" or "DotNet" => "''",
            _ => "''"
        };
    }

    // --- Symbol-table helpers ---

    private static readonly HashSet<string> _alKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "key", "field", "var", "begin", "end", "if", "then", "else", "repeat", "until",
        "while", "do", "for", "to", "downto", "case", "of", "with", "exit", "trigger",
        "procedure", "local", "internal", "protected", "true", "false", "not", "and", "or",
        "xor", "div", "mod", "in", "array", "record", "codeunit", "page", "report", "query",
        "xmlport", "table", "enum", "interface", "temporary", "database"
    };

    private static bool IsAlKeyword(string name) => _alKeywords.Contains(name);

    /// <summary>
    /// Enumerate all codeunits from the BC compiler's symbol table across all loaded packages.
    /// Returns tuples of (id, name, symbolObject) for each codeunit found.
    /// Includes platform/system codeunits that have no SymbolReference.json in their .app package.
    /// </summary>
    private static List<(int Id, string Name, object Symbol)> GetAllCodeunitsFromSymbolTable(Compilation compilation)
    {
        var result = new List<(int, string, object)>();
        try
        {
            var enumMethod = compilation.GetType().GetMethod(
                "GetApplicationObjectTypeSymbolsAcrossModules",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
                null, new[] { typeof(SymbolKind), typeof(bool) }, null);
            if (enumMethod == null) return result;

            var symbolsObj = enumMethod.Invoke(compilation, new object[] { SymbolKind.Codeunit, false });
            if (symbolsObj == null) return result;

            var enumerable = symbolsObj as System.Collections.IEnumerable;
            if (enumerable == null) return result;

            foreach (var sym in enumerable)
            {
                var name = sym.GetType().GetProperty("Name")?.GetValue(sym)?.ToString() ?? "";
                var idProp = sym.GetType().GetProperty("Id");
                if (idProp == null) continue;
                var idObj = idProp.GetValue(sym);
                int id;
                if (idObj is int directId)
                {
                    id = directId;
                }
                else
                {
                    // NavObjectId struct — look for ObjectId or Id sub-property
                    var objectIdProp = idObj?.GetType().GetProperty("ObjectId")
                                    ?? idObj?.GetType().GetProperty("Id");
                    if (objectIdProp == null) continue;
                    if (!int.TryParse(objectIdProp.GetValue(idObj)?.ToString(), out id)) continue;
                }
                if (id <= 0) continue;
                result.Add((id, name, sym));
            }
        }
        catch { /* reflection failure — return what we have */ }
        return result;
    }

    /// <summary>
    /// Render an AL codeunit stub from a BC symbol table object (obtained via reflection).
    /// When <paramref name="sourceTexts"/> is non-null, only methods referenced in source are emitted.
    /// </summary>
    private static string RenderCodeunitFromSymbol(int cuId, string cuName, object cuSymbol, string? sourceTexts)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// Auto-generated stub from BC symbol table — fill in implementations as needed.");
        sb.AppendLine($"codeunit {cuId} \"{EscapeAlString(cuName)}\"");
        sb.AppendLine("{");

        try
        {
            var getMembersMethod = cuSymbol.GetType().GetMethod("GetMembers", Type.EmptyTypes);
            if (getMembersMethod != null)
            {
                var members = getMembersMethod.Invoke(cuSymbol, null) as System.Collections.IEnumerable;
                if (members != null)
                {
                    var emittedSigs = new HashSet<string>();
                    foreach (var member in members)
                    {
                        var memberKind = member.GetType().GetProperty("Kind")?.GetValue(member);
                        if (memberKind?.ToString() != "Method") continue;

                        var methodName = member.GetType().GetProperty("Name")?.GetValue(member)?.ToString();
                        if (methodName == null) continue;

                        // When source filtering is active, skip methods not referenced in source
                        if (sourceTexts != null
                            && !sourceTexts.Contains(methodName + "(", StringComparison.OrdinalIgnoreCase)
                            && !sourceTexts.Contains(methodName + " (", StringComparison.OrdinalIgnoreCase))
                            continue;

                        var paramsProp = member.GetType().GetProperty("Parameters");
                        var paramParts = new List<string>();
                        if (paramsProp != null)
                        {
                            var parms = paramsProp.GetValue(member) as System.Collections.IEnumerable;
                            if (parms != null)
                            {
                                foreach (var p in parms)
                                {
                                    var pName = p.GetType().GetProperty("Name")?.GetValue(p)?.ToString() ?? "p";
                                    if (IsAlKeyword(pName)) pName = $"\"{pName}\"";
                                    else if (pName.Contains(' ') || pName.Contains('.')) pName = $"\"{pName}\"";
                                    var pIsVar = p.GetType().GetProperty("IsVar")?.GetValue(p) is true;
                                    var pType = p.GetType().GetProperty("ParameterType")?.GetValue(p)
                                             ?? p.GetType().GetProperty("Type")?.GetValue(p);
                                    var typeName = pType != null ? RenderSymbolTypeViaReflection(pType) : "Variant";
                                    var prefix = pIsVar ? "var " : "";
                                    paramParts.Add($"{prefix}{pName}: {typeName}");
                                }
                            }
                        }

                        var paramStr = string.Join("; ", paramParts);

                        var returnPart = "";
                        var retType = member.GetType().GetProperty("ReturnValueType")?.GetValue(member)
                                   ?? member.GetType().GetProperty("ReturnType")?.GetValue(member);
                        if (retType != null)
                        {
                            var navTypeKind = retType.GetType().GetProperty("NavTypeKind")?.GetValue(retType);
                            if (navTypeKind != null && navTypeKind.ToString() != "None" && navTypeKind.ToString() != "Void")
                                returnPart = $": {RenderSymbolTypeViaReflection(retType)}";
                        }

                        // Dedup by (name, paramCount) to avoid overloaded identical C# signatures
                        var sig = $"{methodName}/{paramParts.Count}";
                        if (!emittedSigs.Add(sig)) continue;

                        sb.AppendLine($"    procedure {methodName}({paramStr}){returnPart}");
                        sb.AppendLine("    begin");
                        sb.AppendLine("    end;");
                        sb.AppendLine();
                    }
                }
            }
        }
        catch { /* partial output is still useful */ }

        sb.AppendLine("}");
        return sb.ToString();
    }

    /// <summary>Render an AL type from a BC symbol type object using reflection.</summary>
    private static string RenderSymbolTypeViaReflection(object typeSymbol)
    {
        var navTypeKind = typeSymbol.GetType().GetProperty("NavTypeKind")?.GetValue(typeSymbol)?.ToString();
        if (navTypeKind == null) return "Variant";

        return navTypeKind switch
        {
            "Integer" => "Integer",
            "BigInteger" => "BigInteger",
            "Decimal" => "Decimal",
            "Boolean" => "Boolean",
            "Date" => "Date",
            "Time" => "Time",
            "DateTime" => "DateTime",
            "Duration" => "Duration",
            "Guid" => "Guid",
            "Char" => "Char",
            "Byte" => "Byte",
            "Option" => "Option",
            "Enum" => "Option",       // Enum args arrive as NavOption at runtime; Option compiles without package refs
            "Variant" => "Variant",
            "RecordId" => "RecordId",
            "DateFormula" => "DateFormula",
            "JsonObject" => "JsonObject",
            "JsonArray" => "JsonArray",
            "JsonToken" => "JsonToken",
            "JsonValue" => "JsonValue",
            "HttpClient" => "HttpClient",
            "HttpHeaders" => "HttpHeaders",
            "HttpContent" => "HttpContent",
            "HttpRequestMessage" => "HttpRequestMessage",
            "HttpResponseMessage" => "HttpResponseMessage",
            "SecretText" => "SecretText",
            "Text" => "Text",
            "Code" => "Code[20]",
            "Label" => "Text",
            "TextConst" => "Text",
            "InStream" => "InStream",
            "OutStream" => "OutStream",
            "BigText" => "BigText",
            "Blob" => "BigText",
            "XmlDocument" => "XmlDocument",
            _ => "Variant", // Use Variant for complex/unknown types to avoid syntax errors
        };
    }

    // --- Helpers ---

    private static (byte[] Data, int ZipOffset) ReadAppFileHeader(byte[] fileBytes)
    {
        int zipOffset = 0;
        if (fileBytes.Length >= 8
            && fileBytes[0] == (byte)'N' && fileBytes[1] == (byte)'A'
            && fileBytes[2] == (byte)'V' && fileBytes[3] == (byte)'X')
        {
            zipOffset = (int)BitConverter.ToUInt32(fileBytes, 4);
        }
        return (fileBytes, zipOffset);
    }

    private static string SanitizeFileName(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            if (char.IsLetterOrDigit(c) || c == '-' || c == '_')
                sb.Append(c);
        }
        return sb.ToString();
    }

    private static string EscapeAlString(string s)
    {
        return s.Replace("\"", "\"\"");
    }
}
