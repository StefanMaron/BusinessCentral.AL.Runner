/// <summary>
/// Uses AL Dictionaries entirely INSIDE this dependency app — the Pageworks
/// EnsureCustomFont pattern. Every dictionary below is a method local of a
/// codeunit that lives in the dep, so its closed NavObjectDictionary&lt;K,V&gt;
/// instantiations only ever appear in this assembly. The runner's
/// per-instantiation hook scan looks at the app under test and nothing else,
/// so none of these are reachable that way.
/// </summary>
codeunit 61840 "DDC Dep Api"
{
    /// <summary>
    /// Reference-type type arguments (Text, Text). The CLR shares one __Canon
    /// body across all ref/ref instantiations, so this case can be covered
    /// incidentally by a hook installed for some unrelated ref/ref dictionary.
    /// </summary>
    procedure RefTypeRoundTrip(): Text
    var
        D: Dictionary of [Text, Text];
        Got: Text;
    begin
        D.Add('k1', 'v1');
        D.Add('k2', 'v2');
        D.Get('k2', Got);
        exit(Format(D.Count()) + '|' + Got);
    end;

    /// <summary>
    /// Value-type type arguments (Integer, Integer). Each value-type
    /// instantiation gets its OWN native body — no __Canon sharing — so this
    /// case cannot be covered incidentally. It is the one that must fail
    /// before the fix and pass after it.
    /// </summary>
    procedure ValueTypeRoundTrip(): Text
    var
        D: Dictionary of [Integer, Integer];
        Got: Integer;
    begin
        D.Add(1, 100);
        D.Add(2, 200);
        D.Get(2, Got);
        exit(Format(D.Count()) + '|' + Format(Got));
    end;

    /// <summary>
    /// Mixed value-key / ref-value, a third distinct instantiation.
    /// </summary>
    procedure MixedRoundTrip(): Text
    var
        D: Dictionary of [Integer, Text];
        Got: Text;
    begin
        D.Add(7, 'seven');
        D.Get(7, Got);
        exit(Format(D.Count()) + '|' + Got);
    end;

    /// <summary>
    /// Negative direction: the dictionary must behave like a real dictionary
    /// inside the dep too, not like a silently-empty stand-in.
    /// </summary>
    procedure MissingKeyErrorText(): Text
    var
        D: Dictionary of [Integer, Integer];
        Got: Integer;
    begin
        D.Add(1, 100);
        if D.ContainsKey(99) then
            exit('UNEXPECTED-CONTAINSKEY');
        exit('ok');
    end;
}
