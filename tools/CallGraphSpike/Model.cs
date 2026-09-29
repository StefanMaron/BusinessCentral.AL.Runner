namespace CallGraphSpike;

/// <summary>One AL object as the graph sees it. Key is "Kind:id@app" (interfaces have no id: "Interface:name@app").</summary>
sealed class AlObject
{
    public required string Kind;      // Table, TableExtension, Page, PageExtension, Codeunit, Report, ReportExtension, Query, XmlPort, Enum, EnumExtension, Interface
    public required int Id;
    public required string Name;      // normalised (unquoted, lower-case)
    public required string DisplayName;
    public required string App;
    public required string File;
    public int StartLine, EndLine;    // 1-based, the object's own span in File
    public string? ExtendsName;       // extension objects: base object's normalised name
    public bool IsTestCodeunit;
    public string Key => Kind == "Interface" ? $"Interface:{Name}@{App}" : $"{Kind}:{Id}@{App}";

    // Object-level references (forward edges) out of this object, by kind ("*" = any kind).
    public readonly List<Ref> Refs = new();
    // Interfaces this object implements.
    public readonly List<string> Implements = new();
    // Event subscriptions declared here: (publisher kind, publisher name-or-id).
    public readonly List<Ref> Subscribes = new();
    // Non-constant dynamic dispatch sites: kind -> count.
    public readonly Dictionary<string, int> Wildcards = new();
    // Procedures (for test codeunits: procedure-level roots).
    public readonly List<Proc> Procs = new();
    public readonly Dictionary<string, List<Ref>> GlobalVarTypes = new(StringComparer.OrdinalIgnoreCase);
}

readonly record struct Ref(string Kind, string Name, int Id, string Via);

sealed class Proc
{
    public required string Name;
    public string DisplayName = "";
    public bool IsTest;
    public bool IsTrigger;
    public bool IsSubscriber;
    public readonly List<string> Handlers = new();
    public readonly List<Ref> Refs = new();                         // refs from its own signature, locals, body
    public readonly HashSet<string> Identifiers = new(StringComparer.OrdinalIgnoreCase); // every identifier used in its body
    public readonly Dictionary<string, int> Wildcards = new();
}
