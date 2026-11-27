using System.Globalization;
using PixAutomatico.Calendar;

namespace PixAutomatico.Engine;

/// <summary>An amount in centavos.</summary>
/// <param name="Cents">The amount in centavos.</param>
public readonly record struct Money(long Cents) : IComparable<Money>
{
    /// <summary>Zero.</summary>
    public static readonly Money Zero = new(0);

    /// <summary>Whether the amount is zero.</summary>
    public bool IsZero => Cents == 0;

    /// <summary>Formats as "R$ 1.234,56".</summary>
    public override string ToString()
    {
        long m = Math.Abs(Cents);
        string reais = (m / 100).ToString(CultureInfo.InvariantCulture);
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < reais.Length; i++)
        {
            if (i > 0 && (reais.Length - i) % 3 == 0)
            {
                sb.Append('.');
            }

            sb.Append(reais[i]);
        }

        return string.Create(CultureInfo.InvariantCulture, $"{(Cents < 0 ? "-" : "")}R$ {sb},{m % 100:D2}");
    }

    /// <summary>Formats as "1234.56", the API Pix wire format.</summary>
    public string ToDecimalString() => string.Create(CultureInfo.InvariantCulture, $"{Cents / 100}.{Cents % 100:D2}");

    /// <summary>Parses "39.90", "39.9" or "39".</summary>
    public static bool TryParse(string? s, out Money money)
    {
        money = Zero;
        if (string.IsNullOrEmpty(s))
        {
            return false;
        }

        int dot = s.IndexOf('.', StringComparison.Ordinal);
        string whole = dot < 0 ? s : s[..dot];
        string frac = dot < 0 ? "" : s[(dot + 1)..];
        if (whole.Length == 0 || frac.Length > 2 || !whole.All(char.IsAsciiDigit) || !frac.All(char.IsAsciiDigit))
        {
            return false;
        }

        frac = frac.PadRight(2, '0');
        if (!long.TryParse(whole, NumberStyles.None, CultureInfo.InvariantCulture, out long w))
        {
            return false;
        }

        money = new Money((w * 100) + int.Parse(frac, CultureInfo.InvariantCulture));
        return true;
    }

    /// <inheritdoc />
    public int CompareTo(Money other) => Cents.CompareTo(other.Cents);

    /// <summary>Comparison.</summary>
    public static bool operator <(Money a, Money b) => a.Cents < b.Cents;

    /// <summary>Comparison.</summary>
    public static bool operator >(Money a, Money b) => a.Cents > b.Cents;

    /// <summary>Comparison.</summary>
    public static bool operator <=(Money a, Money b) => a.Cents <= b.Cents;

    /// <summary>Comparison.</summary>
    public static bool operator >=(Money a, Money b) => a.Cents >= b.Cents;

    /// <summary>Addition.</summary>
    public static Money operator +(Money a, Money b) => new(a.Cents + b.Cents);

    /// <summary>Subtraction.</summary>
    public static Money operator -(Money a, Money b) => new(a.Cents - b.Cents);
}

/// <summary>Post-due retry policy (API Pix names via <see cref="ApiNames"/>).</summary>
public enum RetryPolicy
{
    /// <summary>NAO_PERMITE.</summary>
    NoRetries,

    /// <summary>PERMITE_3R_7D: up to three dates within seven days after the due date.</summary>
    Retries3R7D,
}

/// <summary>Recurrence status.</summary>
public enum RecStatus
{
    /// <summary>CRIADA.</summary>
    Created,

    /// <summary>APROVADA.</summary>
    Approved,

    /// <summary>REJEITADA.</summary>
    Rejected,

    /// <summary>EXPIRADA.</summary>
    Expired,

    /// <summary>CANCELADA.</summary>
    Cancelled,
}

/// <summary>Journey 1 confirmation request status.</summary>
public enum SolicitationStatus
{
    /// <summary>CRIADA.</summary>
    Created,

    /// <summary>ACEITA.</summary>
    Accepted,

    /// <summary>REJEITADA.</summary>
    Rejected,

    /// <summary>EXPIRADA.</summary>
    Expired,

    /// <summary>CANCELADA.</summary>
    Cancelled,
}

/// <summary>Charge status.</summary>
public enum ChargeStatus
{
    /// <summary>ATIVA: scheduled at the payer's PSP.</summary>
    Active,

    /// <summary>CONCLUIDA: paid.</summary>
    Settled,

    /// <summary>EXPIRADA: no attempt left.</summary>
    Expired,

    /// <summary>REJEITADA: refused at scheduling.</summary>
    Rejected,

    /// <summary>CANCELADA.</summary>
    Cancelled,
}

/// <summary>Why an instruction was sent.</summary>
public enum AttemptType
{
    /// <summary>AGND: original scheduling.</summary>
    Scheduled,

    /// <summary>NTAG: post-due retry.</summary>
    Retry,
}

/// <summary>Attempt status.</summary>
public enum AttemptStatus
{
    /// <summary>AGENDADA.</summary>
    Scheduled,

    /// <summary>LIQUIDADA.</summary>
    Settled,

    /// <summary>NAO_LIQUIDADA.</summary>
    Failed,

    /// <summary>CANCELADA.</summary>
    Cancelled,
}

/// <summary>Who performs a cancellation.</summary>
public enum Party
{
    /// <summary>PAGADOR.</summary>
    Payer,

    /// <summary>RECEBEDOR.</summary>
    Receiver,
}

/// <summary>API Pix names for the engine's enums.</summary>
public static class ApiNames
{
    /// <summary>NAO_PERMITE / PERMITE_3R_7D.</summary>
    public static string ToApi(this RetryPolicy p) => p == RetryPolicy.NoRetries ? "NAO_PERMITE" : "PERMITE_3R_7D";

    /// <summary>CRIADA, APROVADA, ...</summary>
    public static string ToApi(this RecStatus s) => s switch
    {
        RecStatus.Created => "CRIADA",
        RecStatus.Approved => "APROVADA",
        RecStatus.Rejected => "REJEITADA",
        RecStatus.Expired => "EXPIRADA",
        _ => "CANCELADA",
    };

    /// <summary>CRIADA, ACEITA, ...</summary>
    public static string ToApi(this SolicitationStatus s) => s switch
    {
        SolicitationStatus.Created => "CRIADA",
        SolicitationStatus.Accepted => "ACEITA",
        SolicitationStatus.Rejected => "REJEITADA",
        SolicitationStatus.Expired => "EXPIRADA",
        _ => "CANCELADA",
    };

    /// <summary>ATIVA, CONCLUIDA, ...</summary>
    public static string ToApi(this ChargeStatus s) => s switch
    {
        ChargeStatus.Active => "ATIVA",
        ChargeStatus.Settled => "CONCLUIDA",
        ChargeStatus.Expired => "EXPIRADA",
        ChargeStatus.Rejected => "REJEITADA",
        _ => "CANCELADA",
    };

    /// <summary>AGND / NTAG.</summary>
    public static string ToApi(this AttemptType t) => t == AttemptType.Scheduled ? "AGND" : "NTAG";

    /// <summary>AGENDADA, LIQUIDADA, ...</summary>
    public static string ToApi(this AttemptStatus s) => s switch
    {
        AttemptStatus.Scheduled => "AGENDADA",
        AttemptStatus.Settled => "LIQUIDADA",
        AttemptStatus.Failed => "NAO_LIQUIDADA",
        _ => "CANCELADA",
    };

    /// <summary>PAGADOR / RECEBEDOR.</summary>
    public static string ToApi(this Party p) => p == Party.Payer ? "PAGADOR" : "RECEBEDOR";
}

/// <summary>The person or company that owes the charges.</summary>
/// <param name="Document">CPF (11 digits) or CNPJ (14).</param>
/// <param name="Name">Name.</param>
public sealed record Debtor(string Document, string Name);

/// <summary>A recurrence and, once approved, the payer's authorization.</summary>
public sealed record Recurrence
{
    /// <summary>Identifier (idRec).</summary>
    public required string Id { get; init; }

    /// <summary>vinculo.contrato.</summary>
    public required string Contract { get; init; }

    /// <summary>vinculo.objeto.</summary>
    public string Object { get; init; } = "";

    /// <summary>vinculo.devedor.</summary>
    public required Debtor Debtor { get; init; }

    /// <summary>Receiver name shown to the payer.</summary>
    public string Receiver { get; init; } = "";

    /// <summary>calendario.dataInicial.</summary>
    public DateOnly Start { get; set; }

    /// <summary>calendario.dataFinal; null means indefinite.</summary>
    public DateOnly? End { get; init; }

    /// <summary>calendario.periodicidade.</summary>
    public Periodicity Periodicity { get; init; }

    /// <summary>Fixed value of every charge; zero means variable.</summary>
    public Money FixedValue { get; init; }

    /// <summary>Minimum the payer may set as maximum value (variable recurrences).</summary>
    public Money ReceiverFloor { get; init; }

    /// <summary>politicaRetentativa.</summary>
    public RetryPolicy RetryPolicy { get; init; }

    /// <summary>ajusteDiaUtil.</summary>
    public bool AdjustToBusinessDay { get; init; }

    /// <summary>Authorization journey (1–4).</summary>
    public int Journey { get; init; }

    /// <summary>Status.</summary>
    public RecStatus Status { get; set; }

    /// <summary>Maximum value set by the payer; zero means none.</summary>
    public Money PayerMax { get; set; }

    /// <summary>Incremented when the receiver revises the offer.</summary>
    public int Revision { get; set; }

    /// <summary>Creation time.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Last update time.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Who cancelled, if cancelled.</summary>
    public Party? CancelledBy { get; set; }

    /// <summary>Cancellation reason.</summary>
    public string CancelReason { get; set; } = "";

    /// <summary>Whether charges may vary.</summary>
    public bool Variable => FixedValue.IsZero;
}

/// <summary>A journey 1 confirmation request.</summary>
public sealed record Solicitation
{
    /// <summary>Identifier.</summary>
    public required string Id { get; init; }

    /// <summary>Recurrence.</summary>
    public required string RecId { get; init; }

    /// <summary>Recurrence revision offered.</summary>
    public int Revision { get; init; }

    /// <summary>Expiry.</summary>
    public DateTimeOffset Expiry { get; init; }

    /// <summary>Status.</summary>
    public SolicitationStatus Status { get; set; }

    /// <summary>Created.</summary>
    public DateTimeOffset Created { get; init; }

    /// <summary>When it stopped being pending.</summary>
    public DateTimeOffset Ended { get; set; }
}

/// <summary>One settlement attempt.</summary>
public sealed record Attempt
{
    /// <summary>AGND or NTAG.</summary>
    public AttemptType Type { get; init; }

    /// <summary>Settlement date.</summary>
    public DateOnly Date { get; init; }

    /// <summary>Status.</summary>
    public AttemptStatus Status { get; set; }

    /// <summary>Failure reason.</summary>
    public string Reason { get; set; } = "";
}

/// <summary>One cycle's payment.</summary>
public sealed record Charge
{
    /// <summary>txid.</summary>
    public required string TxId { get; init; }

    /// <summary>Recurrence.</summary>
    public required string RecId { get; init; }

    /// <summary>Cycle index.</summary>
    public int Cycle { get; init; }

    /// <summary>Due date.</summary>
    public DateOnly DueDate { get; init; }

    /// <summary>Amount.</summary>
    public Money Amount { get; init; }

    /// <summary>Status.</summary>
    public ChargeStatus Status { get; set; }

    /// <summary>Reason for the final status.</summary>
    public string Reason { get; set; } = "";

    /// <summary>Attempts, oldest first.</summary>
    public List<Attempt> Attempts { get; init; } = [];

    /// <summary>Created.</summary>
    public DateTimeOffset Created { get; init; }

    /// <summary>Settled at.</summary>
    public DateTimeOffset? Settled { get; set; }

    internal Attempt? Last => Attempts.Count == 0 ? null : Attempts[^1];

    internal Charge Copy() => this with { Attempts = Attempts.Select(a => a with { }).ToList() };
}

/// <summary>An engine event, for webhooks, payer notifications and audit.</summary>
/// <param name="At">When.</param>
/// <param name="Kind">e.g. rec.aprovada, cobr.liquidada.</param>
/// <param name="Ref">Recurrence, solicitation, charge or payer document.</param>
/// <param name="Detail">Free text.</param>
public sealed record EngineEvent(DateTimeOffset At, string Kind, string Ref, string Detail)
{
    /// <inheritdoc />
    public override string ToString()
    {
        string s = At.ToOffset(BankCalendar.Brt).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " " + Kind + " " + Ref;
        return Detail.Length > 0 ? s + " " + Detail : s;
    }
}

/// <summary>A rule violation, with its API Pix error type.</summary>
public sealed class PixAutomaticoException : Exception
{
    /// <summary>Creates the exception.</summary>
    public PixAutomaticoException(string type, string detail)
        : base(type + ": " + detail)
    {
        Type = type;
        Detail = detail;
    }

    /// <summary>Creates the exception.</summary>
    public PixAutomaticoException()
        : this("RequisicaoInvalida", "")
    {
    }

    /// <summary>Creates the exception.</summary>
    public PixAutomaticoException(string message)
        : this("RequisicaoInvalida", message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public PixAutomaticoException(string message, Exception innerException)
        : base(message, innerException)
    {
        Type = "RequisicaoInvalida";
        Detail = message;
    }

    /// <summary>NaoEncontrado, RecOperacaoInvalida, SolicRecOperacaoInvalida, CobrOperacaoInvalida or PagamentoNaoRealizado.</summary>
    public string Type { get; }

    /// <summary>Detail in Portuguese.</summary>
    public string Detail { get; }
}
