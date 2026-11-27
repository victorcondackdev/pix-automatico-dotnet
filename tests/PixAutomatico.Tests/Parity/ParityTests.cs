using PixAutomatico.BrCode;
using PixAutomatico.Calendar;
using PixAutomatico.Scenarios;

namespace PixAutomatico.Tests.Parity;

/// <summary>Outputs must match the Go implementation exactly.</summary>
public class ParityTests
{
    [Fact]
    public void HolidaysMatchGoFrom2020To2040()
    {
        var holidays = new NationalHolidays();
        var expected = GoVectors.Holidays.ToDictionary(h => h.Date, h => h.Name);
        for (DateOnly d = new(2020, 1, 1); d < new DateOnly(2041, 1, 1); d = d.AddDays(1))
        {
            string name = holidays.Name(d);
            expected.TryGetValue(BankCalendar.Format(d), out string? want);
            Assert.Equal(want ?? "", name);
        }
    }

    [Fact]
    public void CycleArithmetic()
    {
        var h = new NationalHolidays();
        foreach (var v in GoVectors.Cycles)
        {
            Assert.True(PeriodicityNames.TryParse(v.Periodicity, out Periodicity p));
            DateOnly start = BankCalendar.ParseDate(v.Start);
            Assert.Equal(v.CycleStart, BankCalendar.Format(BankCalendar.CycleStart(start, p, v.K)));
            DateOnly due = BankCalendar.DueDate(h, start, p, v.K, true);
            Assert.Equal(v.DueAdjusted, BankCalendar.Format(due));
            Assert.Equal(v.CycleOfDue, BankCalendar.CycleOf(start, p, due));
        }
    }

    [Fact]
    public void QrPayloads()
    {
        foreach (var v in GoVectors.QrCodes)
        {
            var qr = new AutomaticoQr { MerchantName = v.Name, MerchantCity = v.City, RecUrl = v.RecUrl, CobUrl = v.CobUrl, Amount = v.Amount };
            Assert.Equal(v.Payload, qr.Encode());
            Assert.Equal(v.CobUrl.Length > 0 ? 3 : 2, AutomaticoQr.Journey(AutomaticoQr.Parse(v.Payload)));
        }
    }

    [Fact]
    public void ScenarioTranscripts()
    {
        Assert.True(GoVectors.Scenarios.Length >= 40);
        foreach (var v in GoVectors.Scenarios)
        {
            string got = ScenarioRunner.Run(v.Script.Replace("\r\n", "\n", StringComparison.Ordinal));
            string want = v.Transcript.Replace("\r\n", "\n", StringComparison.Ordinal);
            if (got != want)
            {
                string[] g = got.Split('\n');
                string[] w = want.Split('\n');
                int i = 0;
                while (i < Math.Min(g.Length, w.Length) && g[i] == w[i])
                {
                    i++;
                }

                Assert.Fail($"{v.Name}: first difference at line {i + 1}\n  go:   {(i < w.Length ? w[i] : "<eof>")}\n  .net: {(i < g.Length ? g[i] : "<eof>")}");
            }
        }
    }
}
