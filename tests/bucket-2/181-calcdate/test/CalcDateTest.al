codeunit 60061 "CDF Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "CDF Src";

    [Test]
    procedure CalcDate_AddOneDay()
    begin
        // DMY(15, 6, 2024) + 1D = DMY(16, 6, 2024)
        Assert.AreEqual(DMY2Date(16, 6, 2024), Src.AddOneDay(DMY2Date(15, 6, 2024)),
            'CalcDate(''<+1D>'') must add one day');
    end;

    [Test]
    procedure CalcDate_AddOneDay_AcrossMonthBoundary()
    begin
        // 30-Jun + 1D = 1-Jul
        Assert.AreEqual(DMY2Date(1, 7, 2024), Src.AddOneDay(DMY2Date(30, 6, 2024)),
            'CalcDate(''<+1D>'') must wrap from month end to next month');
    end;

    [Test]
    procedure CalcDate_AddOneMonth()
    begin
        // 15-Jun-2024 + 1M = 15-Jul-2024
        Assert.AreEqual(DMY2Date(15, 7, 2024), Src.AddOneMonth(DMY2Date(15, 6, 2024)),
            'CalcDate(''<+1M>'') must add one month');
    end;

    [Test]
    procedure CalcDate_AddOneYear()
    begin
        Assert.AreEqual(DMY2Date(15, 6, 2025), Src.AddOneYear(DMY2Date(15, 6, 2024)),
            'CalcDate(''<+1Y>'') must add one year');
    end;

    [Test]
    procedure CalcDate_SubtractOneWeek()
    begin
        // 15-Jun-2024 (Saturday) - 1W = 8-Jun-2024
        Assert.AreEqual(DMY2Date(8, 6, 2024), Src.SubtractOneWeek(DMY2Date(15, 6, 2024)),
            'CalcDate(''<-1W>'') must subtract seven days');
    end;

    [Test]
    procedure CalcDate_Compound_MinusYearPlusDay()
    begin
        // 15-Jun-2024 with <-1Y+1D> = 16-Jun-2023
        Assert.AreEqual(DMY2Date(16, 6, 2023), Src.Calc('<-1Y+1D>', DMY2Date(15, 6, 2024)),
            'Compound formula must apply all terms');
    end;

    [Test]
    procedure CalcDate_NotAnIdentityNoOp_NegativeTrap()
    begin
        // Negative trap: if CalcDate were a no-op the add-one-day call would
        // return the input unchanged.
        Assert.AreNotEqual(DMY2Date(15, 6, 2024), Src.AddOneDay(DMY2Date(15, 6, 2024)),
            'CalcDate must not be a pass-through');
    end;

    [Test]
    procedure CalcDate_EmptyFormula_ReturnsBaseDate()
    begin
        // Empty formula = zero offset.
        Assert.AreEqual(DMY2Date(15, 6, 2024), Src.Calc('<>', DMY2Date(15, 6, 2024)),
            'CalcDate with an empty formula must return the base date unchanged');
    end;

    [Test]
    procedure CalcDate_WithDateFormulaType()
    var
        f: DateFormula;
    begin
        // Overload taking DateFormula rather than Text.
        Evaluate(f, '<+1D>');
        Assert.AreEqual(DMY2Date(16, 6, 2024), Src.CalcFormulaType(f, DMY2Date(15, 6, 2024)),
            'CalcDate(DateFormula, Date) must respect the typed formula');
    end;
}
