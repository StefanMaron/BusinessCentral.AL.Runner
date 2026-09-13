report 71844 "WER Layout"
{
    UseRequestPage = false;
    DefaultRenderingLayout = WerExcel;

    dataset
    {
        dataitem(Rows; "WER Row")
        {
            column(No; "No.") { }
        }
    }

    rendering
    {
        layout(WerExcel)
        {
            Type = Excel;
            LayoutFile = 'WerLayout.xlsx';
            Caption = 'WER Excel Layout';
        }
    }
}
