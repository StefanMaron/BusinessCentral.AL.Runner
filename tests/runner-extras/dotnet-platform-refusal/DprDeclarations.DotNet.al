dotnet
{
    assembly("System.Drawing")
    {
        type("System.Drawing.Image"; "DprDrawingImage")
        {
        }
    }
    assembly("mscorlib")
    {
        type("System.Convert"; "DprConvert")
        {
        }
    }
}
