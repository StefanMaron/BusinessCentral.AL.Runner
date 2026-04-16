codeunit 60501 "Test Date Decomposition"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    // --- Date2DMY ---

    [Test]
    procedure Date2DMY_ExtractsDay()
    var
        Helper: Codeunit "Date Decomposition Helper";
    begin
        // 20240115D = January 15, 2024 → day = 15
        Assert.AreEqual(15, Helper.GetDay(20240115D), 'Date2DMY(D,1) must return day 15');
    end;

    [Test]
    procedure Date2DMY_ExtractsMonth()
    var
        Helper: Codeunit "Date Decomposition Helper";
    begin
        // 20240115D = January 15, 2024 → month = 1
        Assert.AreEqual(1, Helper.GetMonth(20240115D), 'Date2DMY(D,2) must return month 1');
    end;

    [Test]
    procedure Date2DMY_ExtractsYear()
    var
        Helper: Codeunit "Date Decomposition Helper";
    begin
        // 20240115D = January 15, 2024 → year = 2024
        Assert.AreEqual(2024, Helper.GetYear(20240115D), 'Date2DMY(D,3) must return year 2024');
    end;

    [Test]
    procedure Date2DMY_DifferentDate_ExtractsCorrectly()
    var
        Helper: Codeunit "Date Decomposition Helper";
    begin
        // 20231231D = December 31, 2023
        Assert.AreEqual(31, Helper.GetDay(20231231D), 'Day of Dec 31 must be 31');
        Assert.AreEqual(12, Helper.GetMonth(20231231D), 'Month of Dec 31 must be 12');
        Assert.AreEqual(2023, Helper.GetYear(20231231D), 'Year of Dec 31 must be 2023');
    end;

    // --- Date2DWY ---

    [Test]
    procedure Date2DWY_ExtractsDayOfWeek()
    var
        Helper: Codeunit "Date Decomposition Helper";
    begin
        // 20240115D = Monday → day-of-week = 1 (BC: 1=Monday)
        Assert.AreEqual(1, Helper.GetDayOfWeek(20240115D), 'Date2DWY(D,1) must return 1 for Monday');
    end;

    [Test]
    procedure Date2DWY_ExtractsWeekNo()
    var
        Helper: Codeunit "Date Decomposition Helper";
    begin
        // 20240115D = January 15, 2024 = week 3
        Assert.AreEqual(3, Helper.GetWeekNo(20240115D), 'Date2DWY(D,2) must return week 3 for Jan 15 2024');
    end;

    [Test]
    procedure Date2DWY_Sunday_ReturnsSeven()
    var
        Helper: Codeunit "Date Decomposition Helper";
    begin
        // 20240114D = January 14, 2024 = Sunday → 7
        Assert.AreEqual(7, Helper.GetDayOfWeek(20240114D), 'Date2DWY(D,1) must return 7 for Sunday');
    end;
}
