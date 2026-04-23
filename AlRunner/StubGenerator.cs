using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AlRunner;

/// <summary>
/// Generates empty AL stub files from .app symbol packages.
/// Reads SymbolReference.json from each .app file and emits one .al file per codeunit.
/// </summary>
public static class StubGenerator
{
    /// <summary>Codeunit IDs that al-runner already mocks natively — skip these.</summary>
    private static readonly HashSet<int> SkipIds = new() { 130, 131004 };

    public record GenerateResult(
        int Generated,
        List<string> SkippedExisting,
        int SkippedNonCodeunit,
        int SkippedNativeMock,
        int SkippedNotReferenced,
        int TotalAvailable,
        int SourceFileCount);

    /// <summary>
    /// Scan all .app files in <paramref name="packagesDir"/>, extract codeunit symbols,
    /// and write one .al stub file per codeunit into <paramref name="outputDir"/>.
    /// When <paramref name="sourceDirs"/> is provided and non-empty, only codeunits
    /// referenced in the AL source are generated.
    /// </summary>
    public static GenerateResult Generate(string packagesDir, string outputDir, IReadOnlyList<string>? sourceDirs = null)
        => Generate(new[] { packagesDir }, outputDir, sourceDirs);

    /// <summary>
    /// Scan all .app files in each of <paramref name="packagesDirs"/>, extract codeunit symbols,
    /// and write one .al stub file per codeunit into <paramref name="outputDir"/>.
    /// When <paramref name="sourceDirs"/> is provided and non-empty, only codeunits
    /// referenced in the AL source are generated.
    /// </summary>
    public static GenerateResult Generate(IReadOnlyList<string> packagesDirs, string outputDir, IReadOnlyList<string>? sourceDirs = null)
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
        var skippedExisting = new List<string>();

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

        return new GenerateResult(generated, skippedExisting, skippedNonCodeunit,
            skippedNativeMock, skippedNotReferenced, totalAvailable, sourceFileCount);
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

    private static string RenderCodeunit(CodeunitSymbol cu, string appName)
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
