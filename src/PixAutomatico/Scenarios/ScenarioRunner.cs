using System.Globalization;
using System.Text;
using PixAutomatico.Calendar;
using PixAutomatico.Engine;

namespace PixAutomatico.Scenarios;

/// <summary>A malformed scenario script (rule violations are part of the transcript instead).</summary>
public sealed class ScenarioException : Exception
{
    /// <summary>Creates the exception.</summary>
    public ScenarioException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public ScenarioException()
        : this("Invalid scenario.")
    {
    }

    /// <summary>Creates the exception.</summary>
    public ScenarioException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Runs Pix Automático scripts and produces transcripts identical to pix-automatico-go's
/// <c>scenario</c> package (same syntax: clock, account, deposit, rec, revise, solicit,
/// cancelsolic, approve, reject, max, cancelrec, charge, retry, cancelcharge, advance, status).
/// </summary>
public sealed class ScenarioRunner
{
    private readonly Dictionary<string, string> _recs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _solics = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _charges = new(StringComparer.Ordinal);
    private readonly StringBuilder _out = new();
    private PixAutomaticoEngine? _engine;
    private int _seen;

    private ScenarioRunner()
    {
    }

    /// <summary>Runs a script and returns its transcript.</summary>
    /// <exception cref="ScenarioException">Syntax errors.</exception>
    public static string Run(string script)
    {
        ArgumentNullException.ThrowIfNull(script);
        var r = new ScenarioRunner();
        string[] lines = script.Split('\n');
        for (int n = 0; n < lines.Length; n++)
        {
            string line = lines[n].Trim();
            int hash = line.IndexOf('#', StringComparison.Ordinal);
            if (hash >= 0)
            {
                line = line[..hash].Trim();
            }

            if (line.Length == 0)
            {
                continue;
            }

            if (r._engine is null && !line.StartsWith("clock ", StringComparison.Ordinal))
            {
                throw new ScenarioException(Inv($"line {n + 1}: script must start with clock"));
            }

            string result;
            try
            {
                result = r.Exec(line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            }
            catch (Exception e) when (e is FormatException or ArgumentException or ScenarioException or KeyNotFoundException)
            {
                throw new ScenarioException(Inv($"line {n + 1}: {e.Message}"), e);
            }

            r._out.Append("> ").Append(line).Append("\n  ").Append(result).Append('\n');
            IReadOnlyList<EngineEvent> evs = r._engine!.Events;
            for (int i = r._seen; i < evs.Count; i++)
            {
                r._out.Append("  · ").Append(evs[i]).Append('\n');
            }

            r._seen = evs.Count;
        }

        return r._out.ToString();
    }

    private static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

    /// <summary>Parses "39.90" into money.</summary>
    public static Money ParseMoney(string s) => Money.TryParse(s, out Money m) ? m : throw new ScenarioException("valor inválido \"" + s + "\"");

    /// <summary>Parses YYYY-MM-DDTHH:MM in Brasília time.</summary>
    public static DateTimeOffset ParseTime(string s)
    {
        DateTime t = DateTime.ParseExact(s, "yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture);
        return new DateTimeOffset(t, BankCalendar.Brt);
    }

    private static Party ParseParty(string s) => s switch
    {
        "PAGADOR" => Party.Payer,
        "RECEBEDOR" => Party.Receiver,
        _ => throw new ScenarioException("parte inválida " + s),
    };

    private static string Outcome(Action a)
    {
        try
        {
            a();
            return "ok";
        }
        catch (PixAutomaticoException e)
        {
            return "erro " + e.Type + ": " + e.Detail;
        }
    }

    private static string Id(Dictionary<string, string> m, string name, string what)
        => m.TryGetValue(name, out string? id) ? id : throw new ScenarioException(what + " \"" + name + "\" não definido");

    private PixAutomaticoEngine E => _engine!;

    private string Exec(string[] f)
    {
        void Need(int n)
        {
            if (f.Length < n)
            {
                throw new ScenarioException(f[0] + ": argumentos insuficientes");
            }
        }

        switch (f[0])
        {
            case "clock":
                Need(2);
                DateTimeOffset start = ParseTime(f[1]);
                if (_engine is not null)
                {
                    throw new ScenarioException("clock só pode ser usado uma vez");
                }

                _engine = new PixAutomaticoEngine(start, "12345678");
                return "ok";
            case "account":
            case "deposit":
                {
                    Need(3);
                    Money a = ParseMoney(f[2]);
                    if (f[0] == "deposit")
                    {
                        E.Deposit(f[1], a);
                        return "ok saldo " + E.Balance(f[1]);
                    }

                    Money lim = f.Length > 3 ? ParseMoney(f[3]) : Money.Zero;
                    E.SetAccount(f[1], a, lim);
                    return "ok";
                }

            case "rec":
                {
                    Need(2);
                    RecRequest req = RecRequestFrom(f[2..]);
                    try
                    {
                        Recurrence rec = E.CreateRecurrence(req);
                        _recs[f[1]] = rec.Id;
                        return "ok " + rec.Id;
                    }
                    catch (PixAutomaticoException e)
                    {
                        return "erro " + e.Type + ": " + e.Detail;
                    }
                }

            case "revise":
                {
                    Need(3);
                    string id = Id(_recs, f[1], "rec");
                    DateOnly d = BankCalendar.ParseDate(f[2]);
                    return Outcome(() => E.ReviseStart(id, d));
                }

            case "solicit":
                {
                    Need(4);
                    string id = Id(_recs, f[1], "rec");
                    DateTimeOffset t = ParseTime(f[3]);
                    try
                    {
                        Solicitation s = E.RequestConfirmation(id, t);
                        _solics[f[2]] = s.Id;
                        return "ok " + s.Id;
                    }
                    catch (PixAutomaticoException e)
                    {
                        return "erro " + e.Type + ": " + e.Detail;
                    }
                }

            case "cancelsolic":
                {
                    Need(2);
                    string id = Id(_solics, f[1], "solicitação");
                    return Outcome(() => E.CancelSolicitation(id));
                }

            case "approve":
                {
                    Need(2);
                    string id = Id(_recs, f[1], "rec");
                    Money max = Money.Zero, imm = Money.Zero;
                    foreach (string kv in f[2..])
                    {
                        int eq = kv.IndexOf('=', StringComparison.Ordinal);
                        string k = eq < 0 ? kv : kv[..eq];
                        Money m = ParseMoney(eq < 0 ? "" : kv[(eq + 1)..]);
                        switch (k)
                        {
                            case "max":
                                max = m;
                                break;
                            case "imediato":
                                imm = m;
                                break;
                            default:
                                throw new ScenarioException("approve: opção \"" + k + "\"");
                        }
                    }

                    return Outcome(() => E.Approve(id, new ApproveOptions(max, imm)));
                }

            case "reject":
                {
                    Need(2);
                    string id = Id(_recs, f[1], "rec");
                    return Outcome(() => E.Reject(id));
                }

            case "max":
                {
                    Need(3);
                    string id = Id(_recs, f[1], "rec");
                    Money m = ParseMoney(f[2]);
                    return Outcome(() => E.SetMaxValue(id, m));
                }

            case "cancelrec":
                {
                    Need(4);
                    string id = Id(_recs, f[1], "rec");
                    Party p = ParseParty(f[2]);
                    string reason = string.Join(' ', f[3..]);
                    return Outcome(() => E.CancelAuthorization(id, p, reason));
                }

            case "charge":
                {
                    Need(5);
                    string id = Id(_recs, f[2], "rec");
                    DateOnly d = BankCalendar.ParseDate(f[3]);
                    Money m = ParseMoney(f[4]);
                    try
                    {
                        Charge c = E.ScheduleCharge(new ChargeRequest(id, d, m));
                        _charges[f[1]] = c.TxId;
                        return "ok " + c.TxId + " " + c.Status.ToApi();
                    }
                    catch (PixAutomaticoException e)
                    {
                        return "erro " + e.Type + ": " + e.Detail;
                    }
                }

            case "retry":
                {
                    Need(3);
                    string id = Id(_charges, f[1], "cobrança");
                    DateOnly d = BankCalendar.ParseDate(f[2]);
                    return Outcome(() => E.RequestRetry(id, d));
                }

            case "cancelcharge":
                {
                    Need(3);
                    string id = Id(_charges, f[1], "cobrança");
                    Party p = ParseParty(f[2]);
                    return Outcome(() => E.CancelCharge(id, p));
                }

            case "advance":
                Need(2);
                E.Advance(ParseTime(f[1]));
                return "ok " + E.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            case "status":
                {
                    Need(2);
                    if (_recs.TryGetValue(f[1], out string? recId))
                    {
                        Recurrence x = E.GetRecurrence(recId);
                        return "rec " + x.Status.ToApi() + " max " + x.PayerMax;
                    }

                    if (_charges.TryGetValue(f[1], out string? txid))
                    {
                        Charge c = E.GetCharge(txid);
                        IEnumerable<string> parts = new[] { "cobr " + c.Status.ToApi() }
                            .Concat(c.Attempts.Select(a => a.Type.ToApi() + ":" + BankCalendar.Format(a.Date) + ":" + a.Status.ToApi()));
                        return string.Join(' ', parts);
                    }

                    throw new ScenarioException("status: \"" + f[1] + "\" não definido");
                }

            default:
                throw new ScenarioException("comando desconhecido \"" + f[0] + "\"");
        }
    }

    private static RecRequest RecRequestFrom(IEnumerable<string> kvs)
    {
        var req = new RecRequest();
        Debtor debtor = new("", "");
        foreach (string kv in kvs)
        {
            int eq = kv.IndexOf('=', StringComparison.Ordinal);
            if (eq < 0)
            {
                throw new ScenarioException("rec: esperado chave=valor em \"" + kv + "\"");
            }

            string k = kv[..eq];
            string v = kv[(eq + 1)..];
            string text = v.Replace('_', ' ');
            req = k switch
            {
                "contrato" => req with { Contract = v },
                "objeto" => req with { Object = text },
                "devedor" => req with { Debtor = debtor = debtor with { Document = v } },
                "nome" => req with { Debtor = debtor = debtor with { Name = text } },
                "recebedor" => req with { Receiver = text },
                "inicio" => req with { Start = BankCalendar.ParseDate(v) },
                "fim" => req with { End = BankCalendar.ParseDate(v) },
                "periodicidade" => req with { Periodicity = v },
                "valor" => req with { FixedValue = ParseMoney(v) },
                "piso" => req with { ReceiverFloor = ParseMoney(v) },
                "retentativa" => req with { RetryPolicy = v },
                "ajuste" => req with { AdjustToBusinessDay = v == "sim" },
                "jornada" => req with { Journey = int.Parse(v, NumberStyles.Integer, CultureInfo.InvariantCulture) },
                _ => throw new ScenarioException("rec: chave desconhecida \"" + k + "\""),
            };
        }

        return req;
    }
}
