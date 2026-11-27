// A streaming subscription billed with Pix Automático, three months fast-forwarded:
// a normal month, a month where the first settlement window fails, and a month
// where the payer cancels the authorization late at night.
//
//   dotnet run --project samples/PixAutomatico.Demo
//   dotnet run --project samples/PixAutomatico.Demo -- script.txt   (run a scenario script)
using PixAutomatico.Calendar;
using PixAutomatico.Engine;
using PixAutomatico.Scenarios;

if (args.Length > 0)
{
    Console.Write(ScenarioRunner.Run(File.ReadAllText(args[0])));
    return;
}

static DateOnly D(string s) => BankCalendar.ParseDate(s);

const string Cpf = "12345678909";
var engine = new PixAutomaticoEngine(D("2026-11-27").At(10, 0), ispb: "12345678");
engine.SetAccount(Cpf, new Money(50_00), dailyLimit: Money.Zero);

Recurrence rec = engine.CreateRecurrence(new RecRequest
{
    Contract = "ASSINATURA-778",
    Object = "Plano Premium",
    Debtor = new Debtor(Cpf, "Maria Souza"),
    Receiver = "Streaming SA",
    Start = D("2026-12-10"),
    Periodicity = "MENSAL",
    FixedValue = new Money(39_90),
    RetryPolicy = "PERMITE_3R_7D",
    Journey = 2,
});
engine.Approve(rec.Id);

foreach ((string scheduleOn, string due) in new[] { ("2026-11-30", "2026-12-10"), ("2026-12-31", "2027-01-10") })
{
    engine.AdvanceTo(D(scheduleOn), 9, 0);
    Charge c = engine.ScheduleCharge(new ChargeRequest(rec.Id, D(due), new Money(39_90)));
    engine.AdvanceTo(D(due), 12, 0);
    if (engine.GetCharge(c.TxId).Status == ChargeStatus.Active)
    {
        Console.WriteLine($"-- {due}: first window failed, the payer tops up before 18:00");
        engine.Deposit(Cpf, new Money(100_00));
    }

    engine.AdvanceTo(D(due), 22, 0);
}

engine.AdvanceTo(D("2027-01-31"), 9, 0);
Charge feb = engine.ScheduleCharge(new ChargeRequest(rec.Id, D("2027-02-10"), new Money(39_90)));
engine.AdvanceTo(D("2027-02-09"), 22, 30);
engine.CancelAuthorization(rec.Id, Party.Payer, "assinatura encerrada");
engine.AdvanceTo(D("2027-02-11"), 0, 0);
Console.WriteLine($"-- February charge after a 22:30 cancellation on the eve: {engine.GetCharge(feb.TxId).Status.ToApi()} (kept: D+2 rule)");

foreach (EngineEvent e in engine.Events)
{
    Console.WriteLine(e);
}
