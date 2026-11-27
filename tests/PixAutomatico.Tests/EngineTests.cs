using PixAutomatico.Calendar;
using PixAutomatico.Engine;

namespace PixAutomatico.Tests;

public class EngineTests
{
    private const string Cpf = "12345678909";

    private static DateOnly D(string s) => BankCalendar.ParseDate(s);

    private static PixAutomaticoEngine NewEngine(long balance = 100000, long limit = 0)
    {
        var e = new PixAutomaticoEngine(D("2026-11-27").At(10, 0), "12345678");
        e.SetAccount(Cpf, new Money(balance), new Money(limit));
        return e;
    }

    private static Recurrence Rec(PixAutomaticoEngine e, Func<RecRequest, RecRequest>? mod = null)
    {
        var req = new RecRequest
        {
            Contract = "CTR-1",
            Object = "Plano Premium",
            Debtor = new Debtor(Cpf, "Fulano"),
            Receiver = "Streaming SA",
            Start = D("2026-12-10"),
            Periodicity = "MENSAL",
            FixedValue = new Money(3990),
            Journey = 2,
        };
        return e.CreateRecurrence(mod is null ? req : mod(req));
    }

    private static PixAutomaticoException Fails(string type, Action a)
    {
        var ex = Assert.Throws<PixAutomaticoException>(a);
        Assert.Equal(type, ex.Type);
        return ex;
    }

    [Fact]
    public void HappyPath()
    {
        var e = NewEngine();
        Recurrence r = Rec(e);
        Assert.StartsWith("RR1234567820261127", r.Id);
        Assert.Equal(29, r.Id.Length);
        e.Approve(r.Id);
        e.AdvanceTo(D("2026-11-30"), 9, 0);
        Charge c = e.ScheduleCharge(new ChargeRequest(r.Id, D("2026-12-10"), new Money(3990)));
        Assert.Equal(ChargeStatus.Active, c.Status);
        e.AdvanceTo(D("2026-12-10"), 9, 0);
        Charge got = e.GetCharge(c.TxId);
        Assert.Equal(ChargeStatus.Settled, got.Status);
        Assert.Equal(0, got.Settled!.Value.Hour);
        Assert.Equal(new Money(100000 - 3990), e.Balance(Cpf));
    }

    [Fact]
    public void SchedulingWindow()
    {
        var e = NewEngine();
        Recurrence r = Rec(e);
        e.Approve(r.Id);
        Assert.Contains("faltam 13", Fails("CobrOperacaoInvalida", () => e.ScheduleCharge(new ChargeRequest(r.Id, D("2026-12-10"), new Money(3990)))).Detail);
        e.AdvanceTo(D("2026-12-09"), 9, 0);
        Assert.Contains("faltam 1", Fails("CobrOperacaoInvalida", () => e.ScheduleCharge(new ChargeRequest(r.Id, D("2026-12-10"), new Money(3990)))).Detail);
    }

    [Fact]
    public void SecondWindowSettlesAfterDeposit()
    {
        var e = NewEngine(balance: 1000);
        Recurrence r = Rec(e);
        e.Approve(r.Id);
        e.AdvanceTo(D("2026-12-01"), 9, 0);
        Charge c = e.ScheduleCharge(new ChargeRequest(r.Id, D("2026-12-10"), new Money(3990)));
        e.AdvanceTo(D("2026-12-10"), 12, 0);
        Assert.Contains(e.Events, ev => ev.Kind == "cobr.janela1.falhou");
        e.Deposit(Cpf, new Money(5000));
        e.AdvanceTo(D("2026-12-10"), 22, 0);
        Charge got = e.GetCharge(c.TxId);
        Assert.Equal(ChargeStatus.Settled, got.Status);
        Assert.Equal(18, got.Settled!.Value.Hour);
    }

    [Fact]
    public void ThreeRetriesThenExpired()
    {
        var e = NewEngine(balance: 0);
        Recurrence r = Rec(e, q => q with { RetryPolicy = "PERMITE_3R_7D" });
        e.Approve(r.Id);
        e.AdvanceTo(D("2026-12-01"), 9, 0);
        Charge c = e.ScheduleCharge(new ChargeRequest(r.Id, D("2026-12-10"), new Money(3990)));
        e.AdvanceTo(D("2026-12-10"), 22, 0);
        Fails("CobrOperacaoInvalida", () => e.RequestRetry(c.TxId, D("2026-12-18")));
        foreach (string day in new[] { "2026-12-11", "2026-12-14", "2026-12-17" })
        {
            e.RequestRetry(c.TxId, D(day));
            e.AdvanceTo(D(day), 22, 0);
        }

        Charge got = e.GetCharge(c.TxId);
        Assert.Equal(ChargeStatus.Expired, got.Status);
        Assert.Equal(4, got.Attempts.Count);
    }

    [Fact]
    public void MaxValueRefusesButKeepsAuthorization()
    {
        var e = NewEngine();
        Recurrence r = Rec(e, q => q with { FixedValue = Money.Zero, ReceiverFloor = new Money(5000) });
        Fails("RecOperacaoInvalida", () => e.Approve(r.Id, new ApproveOptions(new Money(4000))));
        e.Approve(r.Id, new ApproveOptions(new Money(10000)));
        e.AdvanceTo(D("2026-12-01"), 9, 0);
        Charge c = e.ScheduleCharge(new ChargeRequest(r.Id, D("2026-12-10"), new Money(15000)));
        Assert.Equal(ChargeStatus.Rejected, c.Status);
        Assert.Equal(RecStatus.Approved, e.GetRecurrence(r.Id).Status);
        e.SetMaxValue(r.Id, new Money(20000));
        Assert.Equal(ChargeStatus.Active, e.ScheduleCharge(new ChargeRequest(r.Id, D("2026-12-10"), new Money(15000))).Status);
    }

    [Theory]
    [InlineData(21, 59, false)]
    [InlineData(22, 0, true)]
    public void CancellationCutoff(int hour, int minute, bool survives)
    {
        var e = NewEngine();
        Recurrence r = Rec(e, q => q with { Periodicity = "SEMANAL", Start = D("2026-12-04") });
        e.Approve(r.Id);
        e.AdvanceTo(D("2026-12-01"), 9, 0);
        Charge c = e.ScheduleCharge(new ChargeRequest(r.Id, D("2026-12-04"), new Money(3990)));
        e.AdvanceTo(D("2026-12-03"), hour, minute);
        e.CancelAuthorization(r.Id, Party.Payer, "não quero mais");
        e.AdvanceTo(D("2026-12-05"), 0, 0);
        Assert.Equal(survives, e.GetCharge(c.TxId).Status == ChargeStatus.Settled);
    }

    [Fact]
    public void Journey3ImmediatePaymentIsAtomic()
    {
        var e = NewEngine(balance: 1000);
        Recurrence r = Rec(e, q => q with { Journey = 3 });
        Fails("PagamentoNaoRealizado", () => e.Approve(r.Id, new ApproveOptions(ImmediatePayment: new Money(3990))));
        Assert.Equal(RecStatus.Created, e.GetRecurrence(r.Id).Status);
        Assert.Equal(new Money(1000), e.Balance(Cpf));
    }

    [Fact]
    public void Journey1Lifecycle()
    {
        var e = NewEngine();
        Recurrence r = Rec(e, q => q with { Journey = 1 });
        Fails("RecOperacaoInvalida", () => e.Approve(r.Id));
        Fails("SolicRecOperacaoInvalida", () => e.RequestConfirmation(r.Id, e.Now.AddDays(31)));
        Solicitation s = e.RequestConfirmation(r.Id, e.Now.AddDays(3));
        Fails("RecOperacaoInvalida", () => e.ReviseStart(r.Id, D("2026-12-15")));
        e.AdvanceTo(D("2026-12-01"), 0, 0);
        Assert.Equal(SolicitationStatus.Expired, e.GetSolicitation(s.Id).Status);
        Fails("SolicRecOperacaoInvalida", () => e.RequestConfirmation(r.Id, e.Now.AddDays(3)));
        e.ReviseStart(r.Id, D("2026-12-15"));
        Solicitation s2 = e.RequestConfirmation(r.Id, e.Now.AddDays(3));
        e.Approve(r.Id);
        Assert.Equal(SolicitationStatus.Accepted, e.GetSolicitation(s2.Id).Status);
    }

    [Fact]
    public void NotFound() => Fails("NaoEncontrado", () => NewEngine().GetRecurrence("x"));

    [Theory]
    [InlineData(0, "R$ 0,00")]
    [InlineData(3990, "R$ 39,90")]
    [InlineData(123456789, "R$ 1.234.567,89")]
    [InlineData(-5, "-R$ 0,05")]
    public void MoneyFormatting(long cents, string text) => Assert.Equal(text, new Money(cents).ToString());

    [Theory]
    [InlineData("39.90", 3990)]
    [InlineData("39.9", 3990)]
    [InlineData("39", 3900)]
    [InlineData("0.05", 5)]
    public void MoneyParsing(string s, long cents)
    {
        Assert.True(Money.TryParse(s, out Money m));
        Assert.Equal(cents, m.Cents);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1.234")]
    [InlineData("-1")]
    [InlineData("a")]
    public void MoneyRejects(string s) => Assert.False(Money.TryParse(s, out _));
}
