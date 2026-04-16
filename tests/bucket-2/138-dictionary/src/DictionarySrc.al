/// Helper codeunit exercising AL Dictionary operations: Add, Get, Set, Remove,
/// ContainsKey, Count, Keys, Values — the surface named in issue #220.
codeunit 59530 "Dict Helper"
{
    procedure Build(): Dictionary of [Text, Integer]
    var
        d: Dictionary of [Text, Integer];
    begin
        d.Add('one', 1);
        d.Add('two', 2);
        d.Add('three', 3);
        exit(d);
    end;

    procedure CountOf(d: Dictionary of [Text, Integer]): Integer
    begin
        exit(d.Count());
    end;

    procedure GetByKey(d: Dictionary of [Text, Integer]; keyName: Text): Integer
    begin
        exit(d.Get(keyName));
    end;

    procedure ContainsKey(d: Dictionary of [Text, Integer]; keyName: Text): Boolean
    begin
        exit(d.ContainsKey(keyName));
    end;

    /// AL Dictionary.Set returns void — overwrites or adds as needed.
    procedure SetValue(var d: Dictionary of [Text, Integer]; keyName: Text; value: Integer)
    begin
        d.Set(keyName, value);
    end;

    /// AL Dictionary.Remove returns Boolean — true if the key was present.
    procedure RemoveKey(var d: Dictionary of [Text, Integer]; keyName: Text): Boolean
    begin
        exit(d.Remove(keyName));
    end;

    procedure SumValues(d: Dictionary of [Text, Integer]): Integer
    var
        vals: List of [Integer];
        v: Integer;
        total: Integer;
    begin
        vals := d.Values();
        total := 0;
        foreach v in vals do
            total += v;
        exit(total);
    end;

    /// Return whether each of the three build keys is present in d.Keys().
    /// Using a set-membership check (Contains) rather than iteration order.
    procedure AllBuildKeysPresent(d: Dictionary of [Text, Integer]): Boolean
    var
        keys: List of [Text];
    begin
        keys := d.Keys();
        exit(keys.Contains('one') and keys.Contains('two') and keys.Contains('three'));
    end;

    procedure KeysCount(d: Dictionary of [Text, Integer]): Integer
    var
        keys: List of [Text];
    begin
        keys := d.Keys();
        exit(keys.Count());
    end;
}
