using System.Globalization;
using PixAutomatico.Calendar;

namespace PixAutomatico.Engine;

/// <summary>What the receiver sends to create a recurrence. Names use the API Pix vocabulary.</summary>
public sealed record RecRequest
{
    /// <summary>vinculo.contrato.</summary>
    public string Contract { get; init; } = "";

    /// <summary>vinculo.objeto.</summary>
    public string Object { get; init; } = "";

    /// <summary>vinculo.devedor.</summary>
    public Debtor Debtor { get; init; } = new("", "");

    /// <summary>Receiver name.</summary>
    public string Receiver { get; init; } = "";

    /// <summary>calendario.dataInicial.</summary>
    public DateOnly? Start { get; init; }

    /// <summary>calendario.dataFinal.</summary>
    public DateOnly? End { get; init; }

    /// <summary>calendario.periodicidade (SEMANAL, MENSAL, TRIMESTRAL, SEMESTRAL, ANUAL).</summary>
    public string Periodicity { get; init; } = "MENSAL";

    /// <summary>valor.valorRec; zero means variable.</summary>
    public Money FixedValue { get; init; }

    /// <summary>valor.valorMinimoRecebedor.</summary>
    public Money ReceiverFloor { get; init; }

    /// <summary>politicaRetentativa (NAO_PERMITE or PERMITE_3R_7D); empty means NAO_PERMITE.</summary>
    public string RetryPolicy { get; init; } = "";

    /// <summary>ajusteDiaUtil.</summary>
    public bool AdjustToBusinessDay { get; init; }

    /// <summary>Authorization journey, 1–4.</summary>
    public int Journey { get; init; } = 2;
}

/// <summary>The payer's choices when authorizing.</summary>
/// <param name="MaxValue">Maximum per charge (variable recurrences); zero means none.</param>
/// <param name="ImmediatePayment">Journey 3 immediate payment.</param>
public sealed record ApproveOptions(Money MaxValue = default, Money ImmediatePayment = default);

/// <summary>A payment instruction (pain.013) from the receiver.</summary>
/// <param name="RecId">Recurrence.</param>
/// <param name="DueDate">Due date.</param>
/// <param name="Amount">Amount.</param>
/// <param name="TxId">Optional txid; generated when null.</param>
public sealed record ChargeRequest(string RecId, DateOnly DueDate, Money Amount, string? TxId = null);

/// <summary>
/// Pix Automático on a simulated clock, modelling the receiver's and the payer's PSP.
/// A port of pix-automatico-go: the same calls produce the same identifiers, outcomes
/// and event log. Thread-safe; all operations are serialized.
/// </summary>
public sealed class PixAutomaticoEngine
{
    /// <summary>Earliest scheduling, in days before the due date.</summary>
    public const int MinScheduleDays = 2;

    /// <summary>Latest scheduling, in days before the due date.</summary>
    public const int MaxScheduleDays = 10;

    /// <summary>Days after the due date in which retries may happen.</summary>
    public const int RetryDays = 7;

    /// <summary>Distinct retry dates allowed.</summary>
    public const int MaxRetryDates = 3;

    /// <summary>Longest confirmation request, and the identical-offer block.</summary>
    public const int MaxSolicitationDays = 30;

    /// <summary>Cancellations at or after this hour keep the next day's debits.</summary>
    public const int LateCancelHour = 22;

    /// <summary>First settlement window start hour (00:00–08:00).</summary>
    public const int Window1Hour = 0;

    /// <summary>Second settlement window start hour (18:00–21:00).</summary>
    public const int Window2Hour = 18;

    private const string InsufficientFunds = "saldo insuficiente";
    private const string LimitExceeded = "limite do Pix Automático insuficiente";

    private readonly object _gate = new();
    private readonly string _ispb;
    private readonly IHolidays _holidays;
    private readonly Dictionary<string, Recurrence> _recs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Solicitation> _solics = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Charge> _charges = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Account> _accounts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _tried = new(StringComparer.Ordinal);
    private readonly List<EngineEvent> _events = [];
    private DateTimeOffset _now;
    private int _seq;

    private sealed class Account
    {
        public Money Balance;
        public Money DailyLimit;
        public readonly Dictionary<DateOnly, Money> Used = [];
    }

    /// <summary>Creates an engine whose clock starts at <paramref name="start"/>.</summary>
    public PixAutomaticoEngine(DateTimeOffset start, string ispb = "00000000", IHolidays? holidays = null)
    {
        _now = start.ToOffset(BankCalendar.Brt);
        _ispb = string.IsNullOrEmpty(ispb) ? "00000000" : ispb;
        _holidays = holidays ?? new NationalHolidays();
    }

    /// <summary>The calendar used for business-day adjustment.</summary>
    public IHolidays Holidays => _holidays;

    /// <summary>The engine clock.</summary>
    public DateTimeOffset Now
    {
        get
        {
            lock (_gate)
            {
                return _now;
            }
        }
    }

    private DateOnly Today => BankCalendar.DateOf(_now);

    /// <summary>The event log.</summary>
    public IReadOnlyList<EngineEvent> Events
    {
        get
        {
            lock (_gate)
            {
                return _events.ToArray();
            }
        }
    }

    private void Emit(string kind, string reference, string detail) => _events.Add(new EngineEvent(_now, kind, reference, detail));

    private static string D(DateOnly d) => BankCalendar.Format(d);

    private static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

    private string NextId(string prefix)
    {
        _seq++;
        DateOnly d = Today;
        return Inv($"{prefix}{_ispb}{d.Year:D4}{d.Month:D2}{d.Day:D2}{_seq:D11}");
    }

    private static PixAutomaticoException RecError(string detail) => new("RecOperacaoInvalida", detail);

    private static PixAutomaticoException SolicError(string detail) => new("SolicRecOperacaoInvalida", detail);

    private static PixAutomaticoException ChargeError(string detail) => new("CobrOperacaoInvalida", detail);

    private static PixAutomaticoException NotFound(string what, string id) => new("NaoEncontrado", what + " " + id + " não encontrada");

    /// <summary>Creates or replaces a payer account.</summary>
    public void SetAccount(string document, Money balance, Money dailyLimit)
    {
        lock (_gate)
        {
            _accounts[document] = new Account { Balance = balance, DailyLimit = dailyLimit };
        }
    }

    /// <summary>Adds funds to a payer account.</summary>
    public void Deposit(string document, Money amount)
    {
        lock (_gate)
        {
            Acc(document).Balance += amount;
        }
    }

    /// <summary>A payer's balance.</summary>
    public Money Balance(string document)
    {
        lock (_gate)
        {
            return Acc(document).Balance;
        }
    }

    private Account Acc(string doc)
    {
        if (!_accounts.TryGetValue(doc, out Account? a))
        {
            a = new Account();
            _accounts[doc] = a;
        }

        return a;
    }

    private string? Debit(string doc, Money amount, bool automatic)
    {
        Account a = Acc(doc);
        DateOnly d = Today;
        a.Used.TryGetValue(d, out Money used);
        if (automatic && a.DailyLimit.Cents > 0 && (used + amount) > a.DailyLimit)
        {
            return LimitExceeded;
        }

        if (a.Balance < amount)
        {
            return InsufficientFunds;
        }

        a.Balance -= amount;
        if (automatic)
        {
            a.Used[d] = used + amount;
        }

        return null;
    }

    private static bool ValidDoc(string s) => s.Length is 11 or 14 && s.All(char.IsAsciiDigit);

    /// <summary>Creates a recurrence in status CRIADA.</summary>
    /// <exception cref="PixAutomaticoException">A rule is violated.</exception>
    public Recurrence CreateRecurrence(RecRequest r)
    {
        ArgumentNullException.ThrowIfNull(r);
        lock (_gate)
        {
            bool periodicityOk = PeriodicityNames.TryParse(r.Periodicity, out Periodicity p);
            if (string.IsNullOrWhiteSpace(r.Contract))
            {
                throw RecError("vinculo.contrato é obrigatório");
            }

            if (!ValidDoc(r.Debtor.Document))
            {
                throw RecError("vinculo.devedor deve ter CPF ou CNPJ");
            }

            if (!periodicityOk)
            {
                throw RecError("calendario.periodicidade inválida");
            }

            if (r.Start is null || r.Start.Value < Today)
            {
                throw RecError("calendario.dataInicial deve ser hoje ou futura");
            }

            if (r.End is not null && !(r.Start.Value < r.End.Value))
            {
                throw RecError("calendario.dataFinal deve ser posterior à dataInicial");
            }

            if (r.FixedValue.Cents < 0 || r.ReceiverFloor.Cents < 0)
            {
                throw RecError("valores não podem ser negativos");
            }

            if (r.FixedValue.Cents > 0 && r.ReceiverFloor.Cents > 0)
            {
                throw RecError("valorMinimoRecebedor só se aplica a recorrências de valor variável");
            }

            if (r.Journey is < 1 or > 4)
            {
                throw RecError("jornada deve ser 1, 2, 3 ou 4");
            }

            RetryPolicy policy;
            switch (r.RetryPolicy)
            {
                case "":
                case "NAO_PERMITE":
                    policy = RetryPolicy.NoRetries;
                    break;
                case "PERMITE_3R_7D":
                    policy = RetryPolicy.Retries3R7D;
                    break;
                default:
                    throw RecError("politicaRetentativa inválida");
            }

            foreach (Recurrence x in _recs.Values)
            {
                if (x.Contract == r.Contract && x.Debtor.Document == r.Debtor.Document && x.Status is RecStatus.Created or RecStatus.Approved)
                {
                    throw RecError("já existe recorrência ativa para o contrato " + r.Contract);
                }
            }

            var rec = new Recurrence
            {
                Id = NextId("RR"),
                Contract = r.Contract,
                Object = r.Object,
                Debtor = r.Debtor,
                Receiver = r.Receiver,
                Start = r.Start.Value,
                End = r.End,
                Periodicity = p,
                FixedValue = r.FixedValue,
                ReceiverFloor = r.ReceiverFloor,
                RetryPolicy = policy,
                AdjustToBusinessDay = r.AdjustToBusinessDay,
                Journey = r.Journey,
                Status = RecStatus.Created,
                CreatedAt = _now,
                UpdatedAt = _now,
            };
            _recs[rec.Id] = rec;
            string val = rec.Variable ? "variável" : rec.FixedValue.ToString();
            Emit("rec.criada", rec.Id, Inv($"jornada {rec.Journey} {p.ToApi()} {val} {policy.ToApi()}"));
            return rec with { };
        }
    }

    /// <summary>A copy of a recurrence.</summary>
    public Recurrence GetRecurrence(string id)
    {
        lock (_gate)
        {
            return _recs.TryGetValue(id, out Recurrence? r) ? r with { } : throw NotFound("recorrência", id);
        }
    }

    private Recurrence Rec(string id) => _recs.TryGetValue(id, out Recurrence? r) ? r : throw NotFound("recorrência", id);

    private Solicitation? ActiveSolicitation(string recId) => _solics.Values.FirstOrDefault(s => s.RecId == recId && s.Status == SolicitationStatus.Created);

    /// <summary>Changes the first payment date while the recurrence awaits confirmation.</summary>
    public void ReviseStart(string recId, DateOnly start)
    {
        lock (_gate)
        {
            Recurrence r = Rec(recId);
            if (r.Status != RecStatus.Created)
            {
                throw RecError("dataInicial só pode ser alterada antes da confirmação");
            }

            if (start < Today || (r.End is not null && !(start < r.End.Value)))
            {
                throw RecError("dataInicial inválida");
            }

            if (ActiveSolicitation(recId) is { } s)
            {
                throw RecError("cancele a solicitação " + s.Id + " (ERSL) antes de alterar a recorrência");
            }

            r.Start = start;
            r.Revision++;
            r.UpdatedAt = _now;
            Emit("rec.revisada", r.Id, "dataInicial " + D(start));
        }
    }

    /// <summary>Sends a journey 1 confirmation request (pain.009).</summary>
    public Solicitation RequestConfirmation(string recId, DateTimeOffset expiry)
    {
        lock (_gate)
        {
            Recurrence r = Rec(recId);
            if (r.Journey != 1)
            {
                throw SolicError("solicitação de confirmação é exclusiva da jornada 1");
            }

            if (r.Status != RecStatus.Created)
            {
                throw SolicError("recorrência não está aguardando confirmação");
            }

            if (expiry <= _now || expiry - _now > TimeSpan.FromDays(MaxSolicitationDays))
            {
                throw SolicError(Inv($"expiração deve ser futura e em até {MaxSolicitationDays} dias"));
            }

            if (ActiveSolicitation(recId) is { } active)
            {
                throw SolicError("já existe solicitação ativa " + active.Id + " para a recorrência");
            }

            foreach (Solicitation s in _solics.Values)
            {
                if (s.RecId == recId && s.Revision == r.Revision && s.Status is SolicitationStatus.Expired or SolicitationStatus.Rejected
                    && _now - s.Ended < TimeSpan.FromDays(MaxSolicitationDays))
                {
                    throw SolicError(Inv($"oferta idêntica não confirmada há menos de {MaxSolicitationDays} dias; revise a recorrência"));
                }
            }

            var sol = new Solicitation
            {
                Id = NextId("SC"),
                RecId = recId,
                Revision = r.Revision,
                Expiry = expiry.ToOffset(BankCalendar.Brt),
                Status = SolicitationStatus.Created,
                Created = _now,
            };
            _solics[sol.Id] = sol;
            Emit("solicrec.enviada", sol.Id, "rec " + recId + " expira " + sol.Expiry.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
            return sol with { };
        }
    }

    /// <summary>Cancels a pending confirmation request (pain.011).</summary>
    public void CancelSolicitation(string id)
    {
        lock (_gate)
        {
            if (!_solics.TryGetValue(id, out Solicitation? s))
            {
                throw NotFound("solicitação", id);
            }

            if (s.Status != SolicitationStatus.Created)
            {
                throw SolicError("solicitação não está pendente");
            }

            s.Status = SolicitationStatus.Cancelled;
            s.Ended = _now;
            Emit("solicrec.cancelada", s.Id, "");
        }
    }

    /// <summary>A copy of a confirmation request.</summary>
    public Solicitation GetSolicitation(string id)
    {
        lock (_gate)
        {
            return _solics.TryGetValue(id, out Solicitation? s) ? s with { } : throw NotFound("solicitação", id);
        }
    }

    /// <summary>The payer authorizes the recurrence through its journey.</summary>
    public void Approve(string recId, ApproveOptions? options = null)
    {
        ApproveOptions o = options ?? new ApproveOptions();
        lock (_gate)
        {
            Recurrence r = Rec(recId);
            if (r.Status != RecStatus.Created)
            {
                throw RecError("recorrência não está aguardando autorização (status " + r.Status.ToApi() + ")");
            }

            if (!o.MaxValue.IsZero)
            {
                if (!r.Variable)
                {
                    throw RecError("valor máximo só se aplica a recorrências de valor variável");
                }

                if (o.MaxValue < r.ReceiverFloor)
                {
                    throw RecError("valor máximo abaixo do piso do recebedor (" + r.ReceiverFloor + ")");
                }
            }

            Solicitation? solic = null;
            if (r.Journey == 1)
            {
                solic = ActiveSolicitation(recId) ?? throw RecError("jornada 1 exige solicitação de confirmação pendente");
            }
            else if (r.Journey == 3)
            {
                if (o.ImmediatePayment.Cents <= 0)
                {
                    throw RecError("jornada 3 exige o pagamento imediato");
                }

                if (Debit(r.Debtor.Document, o.ImmediatePayment, false) is { } err)
                {
                    Emit("pix.imediato.recusado", r.Id, o.ImmediatePayment + " " + err);
                    throw new PixAutomaticoException("PagamentoNaoRealizado", "pagamento imediato não realizado (" + err + "); autorização não concedida");
                }

                Emit("pix.imediato.liquidado", r.Id, o.ImmediatePayment.ToString());
            }

            if (solic is not null)
            {
                solic.Status = SolicitationStatus.Accepted;
                solic.Ended = _now;
                Emit("solicrec.aceita", solic.Id, "");
            }

            r.Status = RecStatus.Approved;
            r.PayerMax = o.MaxValue;
            r.UpdatedAt = _now;
            Emit("rec.aprovada", r.Id, o.MaxValue.Cents > 0 ? "valor máximo " + o.MaxValue : "");
        }
    }

    /// <summary>The payer refuses the authorization.</summary>
    public void Reject(string recId)
    {
        lock (_gate)
        {
            Recurrence r = Rec(recId);
            if (r.Status != RecStatus.Created)
            {
                throw RecError("recorrência não está aguardando autorização");
            }

            if (ActiveSolicitation(recId) is { } s)
            {
                s.Status = SolicitationStatus.Rejected;
                s.Ended = _now;
                Emit("solicrec.rejeitada", s.Id, "");
            }

            r.Status = RecStatus.Rejected;
            r.UpdatedAt = _now;
            Emit("rec.rejeitada", r.Id, "");
        }
    }

    /// <summary>The payer changes the maximum value; zero removes it.</summary>
    public void SetMaxValue(string recId, Money max)
    {
        lock (_gate)
        {
            Recurrence r = Rec(recId);
            if (r.Status != RecStatus.Approved || !r.Variable)
            {
                throw RecError("valor máximo só pode ser alterado em autorização ativa de valor variável");
            }

            if (!max.IsZero && max < r.ReceiverFloor)
            {
                throw RecError("valor máximo abaixo do piso do recebedor (" + r.ReceiverFloor + ")");
            }

            r.PayerMax = max;
            r.UpdatedAt = _now;
            Emit("rec.valormaximo", r.Id, max.ToString());
        }
    }

    private List<Charge> SortedCharges() => _charges.Values.OrderBy(c => c.TxId, StringComparer.Ordinal).ToList();

    /// <summary>
    /// Cancels the authorization: today's debits are kept; scheduled debits are cancelled
    /// from D+1, or D+2 when cancelled at or after 22:00.
    /// </summary>
    public void CancelAuthorization(string recId, Party by, string reason)
    {
        lock (_gate)
        {
            Recurrence r = Rec(recId);
            if (r.Status is not (RecStatus.Approved or RecStatus.Created))
            {
                throw RecError("recorrência não pode ser cancelada (status " + r.Status.ToApi() + ")");
            }

            if (r.Status == RecStatus.Created && by == Party.Payer)
            {
                throw RecError("o pagador não cancela uma recorrência pendente; ele a rejeita");
            }

            if (ActiveSolicitation(recId) is { } s)
            {
                s.Status = SolicitationStatus.Cancelled;
                s.Ended = _now;
                Emit("solicrec.cancelada", s.Id, "");
            }

            DateOnly from = Today.AddDays(_now.Hour >= LateCancelHour ? 2 : 1);
            r.Status = RecStatus.Cancelled;
            r.CancelledBy = by;
            r.CancelReason = reason;
            r.UpdatedAt = _now;
            Emit("rec.cancelada", r.Id, "por " + by.ToApi() + ": " + reason + "; débitos a partir de " + D(from) + " cancelados");
            foreach (Charge c in SortedCharges())
            {
                if (c.RecId != recId || c.Status != ChargeStatus.Active)
                {
                    continue;
                }

                Attempt a = c.Last!;
                if (a.Status == AttemptStatus.Scheduled && a.Date >= from)
                {
                    a.Status = AttemptStatus.Cancelled;
                    c.Status = ChargeStatus.Cancelled;
                    c.Reason = "autorização cancelada";
                    Emit("cobr.cancelada", c.TxId, "autorização cancelada");
                }
            }
        }
    }

    /// <summary>
    /// Sends a charge to the payer's PSP. A charge above the payer's maximum comes back
    /// REJEITADA (the payer is notified); rule violations throw.
    /// </summary>
    public Charge ScheduleCharge(ChargeRequest req)
    {
        ArgumentNullException.ThrowIfNull(req);
        lock (_gate)
        {
            Recurrence r = Rec(req.RecId);
            if (r.Status != RecStatus.Approved)
            {
                throw ChargeError("recorrência não está aprovada (status " + r.Status.ToApi() + ")");
            }

            if (req.Amount.Cents <= 0)
            {
                throw ChargeError("valor.original deve ser positivo");
            }

            if (!r.Variable && req.Amount != r.FixedValue)
            {
                throw ChargeError("recorrência de valor fixo " + r.FixedValue);
            }

            if (req.DueDate < r.Start || (r.End is not null && req.DueDate > r.End.Value))
            {
                throw ChargeError("dataDeVencimento fora da vigência da recorrência");
            }

            int ahead = Today.DaysUntil(req.DueDate);
            if (ahead is < MinScheduleDays or > MaxScheduleDays)
            {
                throw ChargeError(Inv($"agendamento deve ocorrer entre {MinScheduleDays} e {MaxScheduleDays} dias antes do vencimento (faltam {ahead})"));
            }

            int cycle = BankCalendar.CycleOf(r.Start, r.Periodicity, req.DueDate);
            foreach (Charge existing in _charges.Values)
            {
                if (existing.RecId == r.Id && existing.Cycle == cycle && existing.Status is not (ChargeStatus.Rejected or ChargeStatus.Cancelled))
                {
                    throw ChargeError(Inv($"já existe cobrança {existing.TxId} para o ciclo {cycle}"));
                }
            }

            string txid = req.TxId ?? "";
            if (txid.Length == 0)
            {
                _seq++;
                txid = Inv($"AUT{_ispb}{_seq:D21}");
            }

            if (txid.Length is < 26 or > 35)
            {
                throw ChargeError("txid deve ter de 26 a 35 caracteres");
            }

            if (_charges.ContainsKey(txid))
            {
                throw ChargeError("txid " + txid + " já utilizado");
            }

            var c = new Charge { TxId = txid, RecId = r.Id, Cycle = cycle, DueDate = req.DueDate, Amount = req.Amount, Created = _now };
            _charges[txid] = c;
            if (r.PayerMax.Cents > 0 && req.Amount > r.PayerMax)
            {
                c.Status = ChargeStatus.Rejected;
                c.Reason = "valor acima do máximo definido pelo pagador";
                Emit("cobr.rejeitada", c.TxId, c.Amount + " acima do máximo " + r.PayerMax);
                Emit("notificacao.pagador", r.Debtor.Document, "cobrança de " + c.Amount + " acima do seu valor máximo " + r.PayerMax + ": revise o limite ou pague por outro meio");
                return c.Copy();
            }

            c.Status = ChargeStatus.Active;
            c.Attempts.Add(new Attempt { Type = AttemptType.Scheduled, Date = req.DueDate, Status = AttemptStatus.Scheduled });
            Emit("cobr.agendada", c.TxId, Inv($"{c.Amount} vencimento {D(c.DueDate)} ciclo {cycle}"));
            Account a = Acc(r.Debtor.Document);
            if (a.DailyLimit.Cents > 0 && req.Amount > a.DailyLimit)
            {
                Emit("notificacao.pagador", r.Debtor.Document, "agendamento de " + c.Amount + " acima do seu limite diário do Pix Automático " + a.DailyLimit);
            }

            return c.Copy();
        }
    }

    /// <summary>A copy of a charge.</summary>
    public Charge GetCharge(string txid)
    {
        lock (_gate)
        {
            return _charges.TryGetValue(txid, out Charge? c) ? c.Copy() : throw NotFound("cobrança", txid);
        }
    }

    private Charge ChargeById(string txid) => _charges.TryGetValue(txid, out Charge? c) ? c : throw NotFound("cobrança", txid);

    /// <summary>Requests a post-due retry (NTAG).</summary>
    public void RequestRetry(string txid, DateOnly date)
    {
        lock (_gate)
        {
            Charge c = ChargeById(txid);
            Recurrence r = _recs[c.RecId];
            if (r.RetryPolicy != RetryPolicy.Retries3R7D)
            {
                throw ChargeError("recorrência não permite novas tentativas");
            }

            if (r.Status != RecStatus.Approved)
            {
                throw ChargeError("autorização não está ativa");
            }

            if (c.Status != ChargeStatus.Active || c.Last!.Status != AttemptStatus.Failed)
            {
                throw ChargeError("nova tentativa só após tentativa não liquidada");
            }

            if (!(date > c.DueDate) || date > c.DueDate.AddDays(RetryDays))
            {
                throw ChargeError(Inv($"nova tentativa deve ser em até {RetryDays} dias após o vencimento"));
            }

            if (!(date > Today))
            {
                throw ChargeError("nova tentativa deve ser solicitada até 23:59 do dia anterior");
            }

            int n = c.Attempts.Count(a => a.Type == AttemptType.Retry);
            if (n >= MaxRetryDates)
            {
                throw ChargeError(Inv($"limite de {MaxRetryDates} novas tentativas excedido"));
            }

            c.Attempts.Add(new Attempt { Type = AttemptType.Retry, Date = date, Status = AttemptStatus.Scheduled });
            _tried[c.TxId] = 0;
            Emit("cobr.retentativa", c.TxId, Inv($"{n + 1}/{MaxRetryDates} em {D(date)}"));
        }
    }

    /// <summary>
    /// Cancels a scheduled charge: the receiver until the eve of the due date (never during
    /// retries), the payer until 23:59 of the day before the debit.
    /// </summary>
    public void CancelCharge(string txid, Party by)
    {
        lock (_gate)
        {
            Charge c = ChargeById(txid);
            if (c.Status != ChargeStatus.Active)
            {
                throw ChargeError("cobrança não está ativa");
            }

            Attempt a = c.Last!;
            if (a.Status != AttemptStatus.Scheduled)
            {
                throw ChargeError("não há débito agendado");
            }

            DateOnly limit = a.Date;
            if (by == Party.Receiver)
            {
                if (a.Type == AttemptType.Retry)
                {
                    throw ChargeError("o recebedor não pode cancelar durante as novas tentativas");
                }

                limit = c.DueDate;
            }

            if (!(Today < limit))
            {
                throw ChargeError("cancelamento permitido somente até a véspera da data do débito");
            }

            a.Status = AttemptStatus.Cancelled;
            c.Status = ChargeStatus.Cancelled;
            c.Reason = "cancelada pelo " + by.ToApi().ToLowerInvariant();
            Emit("cobr.cancelada", c.TxId, "por " + by.ToApi());
        }
    }

    private (DateTimeOffset At, Action Act)? NextAction(DateTimeOffset limit)
    {
        DateTimeOffset best = default;
        Action? act = null;
        void Consider(DateTimeOffset t, Action f)
        {
            if (t > limit)
            {
                return;
            }

            if (t < _now)
            {
                t = _now;
            }

            if (act is null || t < best)
            {
                best = t;
                act = f;
            }
        }

        foreach (Solicitation s in _solics.Values.Where(x => x.Status == SolicitationStatus.Created).OrderBy(x => x.Id, StringComparer.Ordinal))
        {
            Consider(s.Expiry, () => ExpireSolicitation(s));
        }

        foreach (Charge c in SortedCharges())
        {
            if (c.Status != ChargeStatus.Active)
            {
                continue;
            }

            Attempt a = c.Last!;
            if (a.Status == AttemptStatus.Scheduled)
            {
                _tried.TryGetValue(c.TxId, out int tried);
                if (tried == 0)
                {
                    Consider(a.Date.At(Window1Hour, 0), () => Settle(c, 1));
                }
                else
                {
                    Consider(a.Date.At(Window2Hour, 0), () => Settle(c, 2));
                }
            }
            else if (a.Status == AttemptStatus.Failed)
            {
                Recurrence r = _recs[c.RecId];
                int retries = c.Attempts.Count(x => x.Type == AttemptType.Retry);
                if (r.RetryPolicy != RetryPolicy.Retries3R7D || r.Status != RecStatus.Approved || retries >= MaxRetryDates)
                {
                    Consider(_now, () => ExpireCharge(c, "sem novas tentativas"));
                }
                else
                {
                    Consider(c.DueDate.AddDays(RetryDays + 1).At(0, 0), () => ExpireCharge(c, "prazo de novas tentativas encerrado"));
                }
            }
        }

        return act is null ? null : (best, act);
    }

    /// <summary>Moves the clock to t, running every window, retry and expiry on the way, in time order.</summary>
    public void Advance(DateTimeOffset t)
    {
        lock (_gate)
        {
            t = t.ToOffset(BankCalendar.Brt);
            while (NextAction(t) is { } next)
            {
                _now = next.At;
                next.Act();
            }

            if (t > _now)
            {
                _now = t;
            }
        }
    }

    /// <summary>Advance to hh:mm of a date (Brasília time).</summary>
    public void AdvanceTo(DateOnly date, int hour, int minute) => Advance(date.At(hour, minute));

    private void ExpireSolicitation(Solicitation s)
    {
        s.Status = SolicitationStatus.Expired;
        s.Ended = _now;
        Emit("solicrec.expirada", s.Id, "");
    }

    private void ExpireCharge(Charge c, string why)
    {
        c.Status = ChargeStatus.Expired;
        c.Reason = why;
        Emit("cobr.expirada", c.TxId, why);
    }

    private void Settle(Charge c, int window)
    {
        Recurrence r = _recs[c.RecId];
        Attempt a = c.Last!;
        _tried[c.TxId] = window;
        string? err = Debit(r.Debtor.Document, c.Amount, true);
        if (err is null)
        {
            a.Status = AttemptStatus.Settled;
            c.Status = ChargeStatus.Settled;
            c.Settled = _now;
            Emit("cobr.liquidada", c.TxId, Inv($"{c.Amount} janela {window} ({a.Type.ToApi()})"));
            return;
        }

        if (window == 1)
        {
            Emit("cobr.janela1.falhou", c.TxId, err);
            Emit("notificacao.pagador", r.Debtor.Document, "débito de " + c.Amount + " para " + r.Receiver + " não realizado (" + err + "); nova tentativa às 18h");
            return;
        }

        a.Status = AttemptStatus.Failed;
        a.Reason = err;
        _tried[c.TxId] = 0;
        Emit("cobr.nao_liquidada", c.TxId, a.Type.ToApi() + " " + err);
    }
}
