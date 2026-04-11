codeunit 50521 "RWK Filter Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    local procedure SeedFour()
    var
        R: Record "RWK Row";
    begin
        R.Id := 1; R.Name := 'alpha'; R.Insert();
        R.Init();
        R.Id := 2; R.Name := 'beta'; R.Insert();
        R.Init();
        R.Id := 7; R.Name := 'gamma'; R.Insert();
        R.Init();
        R.Id := 12; R.Name := 'delta'; R.Insert();
    end;

    [Test]
    procedure SetFilterAndWithNotEqualExclusion()
    var
        R: Record "RWK Row";
    begin
        SeedFour();
        // Id <> 2 AND Id <> 12 — should match Id=1 and Id=7.
        R.SetFilter(Id, '<>%1&<>%2', 2, 12);
        Assert.AreEqual(2, R.Count(), 'AND filter should exclude 2 and 12, leaving 1 and 7');
    end;

    [Test]
    procedure SetFilterAndOnIntegerFieldBounds()
    var
        R: Record "RWK Row";
    begin
        SeedFour();
        // Id >= 5 AND Id <= 10 — should match Id=7 only.
        R.SetFilter(Id, '>=5&<=10');
        Assert.AreEqual(1, R.Count(), 'AND range filter should match only Id=7');
    end;

    [Test]
    procedure SetFilterOrStillWorks()
    var
        R: Record "RWK Row";
    begin
        SeedFour();
        R.SetFilter(Id, '1|7');
        Assert.AreEqual(2, R.Count(), 'OR filter should match 1 and 7');
    end;

    [Test]
    procedure SetFilterOrAndPrecedence()
    var
        R: Record "RWK Row";
    begin
        SeedFour();
        // BC precedence: AND binds tighter than OR.
        // 1 | >=5&<=10  -> (1) OR (5..10) -> matches 1 and 7
        R.SetFilter(Id, '1|>=5&<=10');
        Assert.AreEqual(2, R.Count(), 'OR of (value) and (AND range) should match 1 and 7');
    end;

    [Test]
    procedure SetFilterRangeOperator()
    var
        R: Record "RWK Row";
    begin
        SeedFour();
        R.SetFilter(Id, '2..10');
        Assert.AreEqual(2, R.Count(), '.. range should match 2 and 7');
    end;

    [Test]
    procedure SetFilterWildcardStar()
    var
        R: Record "RWK Row";
    begin
        SeedFour();
        R.SetFilter(Name, '@*a*');
        // Every seeded name contains an 'a': alpha, beta, gamma, delta
        Assert.AreEqual(4, R.Count(), 'Wildcard @*a* should match every row');
    end;

    [Test]
    procedure SetFilterCaseInsensitivePrefix()
    var
        R: Record "RWK Row";
    begin
        SeedFour();
        R.SetFilter(Name, '@ALPHA');
        Assert.AreEqual(1, R.Count(), '@ case-insensitive should match alpha/ALPHA');
    end;

    [Test]
    procedure SetFilterCombinesAcrossFields()
    var
        R: Record "RWK Row";
    begin
        SeedFour();
        // Per-field filters AND-combine: Id >= 5 AND Name starts with g*.
        R.SetFilter(Id, '>=5');
        R.SetFilter(Name, 'g*');
        Assert.AreEqual(1, R.Count(), 'Per-field filters should AND-combine to just Id=7');
    end;

    [Test]
    procedure SetFilterEmptyExpressionMatchesAll()
    var
        R: Record "RWK Row";
    begin
        SeedFour();
        R.SetFilter(Name, '');
        Assert.AreEqual(4, R.Count(), 'Empty filter should match every row');
    end;

    [Test]
    procedure SetFilterPlaceholderInteger()
    var
        R: Record "RWK Row";
        Low: Integer;
        High: Integer;
    begin
        SeedFour();
        Low := 2;
        High := 10;
        // Two integer placeholders substituted into an AND expression.
        R.SetFilter(Id, '>=%1&<=%2', Low, High);
        Assert.AreEqual(2, R.Count(), '%%1..%%2 integer substitution should match 2 and 7');
    end;

    [Test]
    procedure SetFilterPlaceholderText()
    var
        R: Record "RWK Row";
        Target: Text;
    begin
        SeedFour();
        Target := 'beta';
        R.SetFilter(Name, '%1', Target);
        Assert.AreEqual(1, R.Count(), 'Text placeholder should match the literal value');
    end;

    [Test]
    procedure SetFilterFourPlaceholdersOrChain()
    var
        R: Record "RWK Row";
    begin
        SeedFour();
        // Four placeholders in an OR chain — %1|%2|%3|%4.
        R.SetFilter(Id, '%1|%2|%3|%4', 1, 2, 7, 99);
        // 99 isn't in the seed set, so 3 rows should match.
        Assert.AreEqual(3, R.Count(), '%%1..%%4 OR chain should match 1, 2 and 7');
    end;

    [Test]
    procedure SetFilterAndWithOptionMemberNameLiterals()
    var
        R: Record "RWK Row";
    begin
        // #19 regression: AL filter literals like `<>Red&<>Blue` must
        // resolve option member names to ordinals, not compare against the
        // stored NavOption string form (which is the ordinal).
        R.Id := 1; R.Kind := R.Kind::Red; R.Insert();
        R.Init(); R.Id := 2; R.Kind := R.Kind::Green; R.Insert();
        R.Init(); R.Id := 3; R.Kind := R.Kind::Blue; R.Insert();
        R.Init(); R.Id := 4; R.Kind := R.Kind::Yellow; R.Insert();

        R.SetFilter(Kind, '<>Red&<>Blue');
        Assert.AreEqual(2, R.Count(), 'Option member name literals in AND filter — Green and Yellow should remain');
    end;

    [Test]
    procedure SetFilterPlaceholderInOrWithAnd()
    var
        R: Record "RWK Row";
    begin
        SeedFour();
        // Mixes placeholders with precedence: %1 | (>=%2 & <=%3).
        R.SetFilter(Id, '%1|>=%2&<=%3', 1, 7, 12);
        Assert.AreEqual(3, R.Count(), 'Precedence with placeholders should match 1, 7, 12');
    end;
}
