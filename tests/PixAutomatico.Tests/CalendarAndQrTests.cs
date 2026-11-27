using PixAutomatico.BrCode;
using PixAutomatico.Calendar;
using PixAutomatico.Scenarios;

namespace PixAutomatico.Tests;

public class CalendarAndQrTests
{
    [Theory]
    [InlineData(2024, "2024-03-31")]
    [InlineData(2025, "2025-04-20")]
    [InlineData(2026, "2026-04-05")]
    [InlineData(2027, "2027-03-28")]
    public void Easter(int year, string date) => Assert.Equal(date, BankCalendar.Format(BankCalendar.Easter(year)));

    [Fact]
    public void MonthlyClamps()
    {
        DateOnly start = BankCalendar.ParseDate("2026-01-31");
        Assert.Equal("2026-02-28", BankCalendar.Format(BankCalendar.CycleStart(start, Periodicity.Monthly, 1)));
        Assert.Equal("2026-03-31", BankCalendar.Format(BankCalendar.CycleStart(start, Periodicity.Monthly, 2)));
        Assert.Equal("2028-02-29", BankCalendar.Format(BankCalendar.CycleStart(start, Periodicity.Monthly, 25)));
    }

    [Fact]
    public void DueDateMovesBackRatherThanLeaveItsCycle()
    {
        var extra = new Dictionary<DateOnly, string>();
        for (DateOnly d = BankCalendar.ParseDate("2026-12-07"); d < BankCalendar.ParseDate("2026-12-14"); d = d.AddDays(1))
        {
            extra[d] = "recesso";
        }

        var h = new NationalHolidays(extra);
        Assert.Equal("2026-12-04", BankCalendar.Format(BankCalendar.DueDate(h, BankCalendar.ParseDate("2026-11-30"), Periodicity.Weekly, 1, true)));
    }

    [Fact]
    public void PublishedJourney2Payload()
    {
        const string published = "00020126180014br.gov.bcb.pix5204000053039865802BR5913Fulano de Tal6008BRASILIA62070503***80800014br.gov.bcb.pix2558pix.example.com/qr/v2/rec/2353c790eefb11eaadc10242ac120002630462C9";
        var qr = new AutomaticoQr { MerchantName = "Fulano de Tal", MerchantCity = "BRASILIA", RecUrl = "pix.example.com/qr/v2/rec/2353c790eefb11eaadc10242ac120002" };
        Assert.Equal(published, qr.Encode());
        Assert.Equal(2, AutomaticoQr.Journey(AutomaticoQr.Parse(published)));
        Assert.Throws<BrCodeException>(() => AutomaticoQr.Parse(published[..^1] + "0"));
        Assert.Equal(0x29B1, AutomaticoQr.Crc16("123456789"));
    }

    [Theory]
    [InlineData("rec x")]
    [InlineData("clock nope")]
    [InlineData("clock 2026-11-27T10:00\nfly")]
    [InlineData("clock 2026-11-27T10:00\napprove ghost")]
    public void ScriptErrors(string script) => Assert.Throws<ScenarioException>(() => ScenarioRunner.Run(script));
}
