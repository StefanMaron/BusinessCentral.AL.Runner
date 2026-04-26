report 95000 "RS Rendering Report"
{
    DefaultRenderingLayout = ExcelLayout;

    dataset
    {
        dataitem(RSTestData; "RS Test Record")
        {
        }
    }

    rendering
    {
        layout(ExcelLayout)
        {
            Type = Excel;
            LayoutFile = 'MyLayout.xlsx';
            Caption = 'Test Excel Layout';
        }
        layout(WordLayout)
        {
            Type = Word;
            LayoutFile = 'MyLayout.docx';
            Caption = 'Test Word Layout';
        }
    }
}
