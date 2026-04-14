codeunit 50909 "Product Filter Tests"
{
    Subtype = Test;

    var
        ProductQuery: Codeunit "Product Query";
        Assert: Codeunit Assert;

    local procedure SetupProducts()
    begin
        CreateProduct('WIDGET-A', 'Blue Widget', 'WIDGETS', 10.00, 100);
        CreateProduct('WIDGET-B', 'Red Widget', 'WIDGETS', 25.00, 50);
        CreateProduct('GADGET-A', 'Super Gadget', 'GADGETS', 50.00, 30);
        CreateProduct('GADGET-B', 'Mini Gadget', 'GADGETS', 15.00, 200);
        CreateProduct('TOOL-A', 'Power Tool', 'TOOLS', 75.00, 10);
        CreateProduct('TOOL-B', 'Hand Tool', 'TOOLS', 5.00, 500);
    end;

    [Test]
    procedure TestWildcardContains()
    var
        Product: Record "Test Product";
    begin
        // [GIVEN] Products with various descriptions
        SetupProducts();

        // [WHEN] Filtering description containing "Widget"
        Product.SetFilter("Description", '*Widget*');

        // [THEN] Should find the 2 widget products
        Assert.AreEqual(2, Product.Count(), 'Should find 2 products containing Widget');
    end;

    [Test]
    procedure TestWildcardStartsWith()
    var
        Product: Record "Test Product";
    begin
        SetupProducts();

        // [WHEN] Filtering descriptions starting with "Super"
        Product.SetFilter("Description", 'Super*');

        // [THEN] Should find 1 product
        Assert.AreEqual(1, Product.Count(), 'Should find 1 product starting with Super');
    end;

    [Test]
    procedure TestWildcardEndsWith()
    var
        Product: Record "Test Product";
    begin
        SetupProducts();

        // [WHEN] Filtering descriptions ending with "Tool"
        Product.SetFilter("Description", '*Tool');

        // [THEN] Should find 2 products (Power Tool, Hand Tool)
        Assert.AreEqual(2, Product.Count(), 'Should find 2 products ending with Tool');
    end;

    [Test]
    procedure TestOrPipeFilter()
    begin
        SetupProducts();

        // [WHEN] Filtering category with OR pipe
        // [THEN] Should find 4 products in WIDGETS or GADGETS
        Assert.AreEqual(4, ProductQuery.CountByCategory('WIDGETS|GADGETS'),
            'Should find 4 products in WIDGETS or GADGETS');
    end;

    [Test]
    procedure TestNotEqualFilter()
    begin
        SetupProducts();

        // [WHEN] Filtering category not equal to TOOLS
        // [THEN] Should find 4 products (WIDGETS + GADGETS)
        Assert.AreEqual(4, ProductQuery.CountByCategory('<>TOOLS'),
            'Should find 4 products not in TOOLS');
    end;

    [Test]
    procedure TestGreaterThanOrEqualFilter()
    begin
        SetupProducts();

        // [WHEN] Filtering price >= 50
        // [THEN] Should find 2 products (Super Gadget at 50, Power Tool at 75)
        Assert.AreEqual(2, ProductQuery.CountByPriceFilter('>=50'),
            'Should find 2 products with price >= 50');
    end;

    [Test]
    procedure TestLessThanOrEqualFilter()
    begin
        SetupProducts();

        // [WHEN] Filtering price <= 15
        // [THEN] Should find 3 products (Blue Widget 10, Mini Gadget 15, Hand Tool 5)
        Assert.AreEqual(3, ProductQuery.CountByPriceFilter('<=15'),
            'Should find 3 products with price <= 15');
    end;

    [Test]
    procedure TestRangeFilter()
    begin
        SetupProducts();

        // [WHEN] Filtering stock in range 30..100
        // [THEN] Should find 3 products (100, 50, 30)
        Assert.AreEqual(3, ProductQuery.CountByStockFilter('30..100'),
            'Should find 3 products with stock between 30 and 100');
    end;

    [Test]
    procedure TestCaseInsensitiveFilter()
    var
        Product: Record "Test Product";
    begin
        SetupProducts();

        // [WHEN] Filtering description case-insensitively for "blue widget"
        Product.SetFilter("Description", '@blue widget');

        // [THEN] Should find 1 product despite case mismatch
        Assert.AreEqual(1, Product.Count(), 'Case-insensitive filter should find Blue Widget');
    end;

    [Test]
    procedure TestCaseInsensitiveWildcard()
    var
        Product: Record "Test Product";
    begin
        SetupProducts();

        // [WHEN] Filtering description case-insensitively with wildcard
        Product.SetFilter("Description", '@*gadget*');

        // [THEN] Should find 2 gadget products
        Assert.AreEqual(2, Product.Count(), 'Case-insensitive wildcard should find 2 Gadgets');
    end;

    local procedure CreateProduct(ProductCode: Code[20]; Description: Text[100]; Category: Code[20]; Price: Decimal; Stock: Integer)
    var
        Product: Record "Test Product";
    begin
        Product.Init();
        Product."Code" := ProductCode;
        Product."Description" := Description;
        Product."Category" := Category;
        Product."Price" := Price;
        Product."Stock" := Stock;
        Product.Insert(true);
    end;
}
