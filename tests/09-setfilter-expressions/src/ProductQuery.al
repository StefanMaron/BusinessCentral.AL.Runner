codeunit 50109 "Product Query"
{
    procedure CountByCategory(CategoryFilter: Text): Integer
    var
        Product: Record "Test Product";
    begin
        Product.SetFilter("Category", CategoryFilter);
        exit(Product.Count());
    end;

    procedure CountByPriceFilter(PriceFilter: Text): Integer
    var
        Product: Record "Test Product";
    begin
        Product.SetFilter("Price", PriceFilter);
        exit(Product.Count());
    end;

    procedure CountByDescriptionFilter(DescFilter: Text): Integer
    var
        Product: Record "Test Product";
    begin
        Product.SetFilter("Description", DescFilter);
        exit(Product.Count());
    end;

    procedure CountByStockFilter(StockFilter: Text): Integer
    var
        Product: Record "Test Product";
    begin
        Product.SetFilter("Stock", StockFilter);
        exit(Product.Count());
    end;
}
