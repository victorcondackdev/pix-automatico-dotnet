using System.Globalization;
using System.Text;

namespace PixAutomatico.BrCode;

/// <summary>A malformed QR payload.</summary>
public sealed class BrCodeException : Exception
{
    /// <summary>Creates the exception.</summary>
    public BrCodeException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public BrCodeException()
        : this("Invalid BR Code.")
    {
    }

    /// <summary>Creates the exception.</summary>
    public BrCodeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>One top-level TLV field.</summary>
/// <param name="Id">Two-digit ID.</param>
/// <param name="Value">Value.</param>
public sealed record BrCodeField(string Id, string Value);

/// <summary>
/// QR payloads ("Pix Copia e Cola") of the Pix Automático journeys: journey 2 (recurrence
/// only), 3 (immediate payment plus recurrence) and 4 (charge with due date plus
/// recurrence). The recurrence travels in template 80; the payment, if any, in 26.
/// </summary>
public sealed record AutomaticoQr
{
    /// <summary>The Pix GUI.</summary>
    public const string Gui = "br.gov.bcb.pix";

    /// <summary>Field 59, 1–25 ASCII characters.</summary>
    public required string MerchantName { get; init; }

    /// <summary>Field 60, 1–15 ASCII characters.</summary>
    public required string MerchantCity { get; init; }

    /// <summary>Recurrence location without scheme.</summary>
    public required string RecUrl { get; init; }

    /// <summary>Payment location without scheme (journeys 3 and 4).</summary>
    public string CobUrl { get; init; } = "";

    /// <summary>Optional amount, e.g. "39.90".</summary>
    public string Amount { get; init; } = "";

    private static string Tlv(string id, string v) => id + v.Length.ToString("D2", CultureInfo.InvariantCulture) + v;

    private static bool Ascii(string s) => s.All(c => c is >= ' ' and <= '~');

    /// <summary>CRC-16/CCITT-FALSE.</summary>
    public static ushort Crc16(string s)
    {
        ArgumentNullException.ThrowIfNull(s);
        int crc = 0xFFFF;
        foreach (byte b in Encoding.ASCII.GetBytes(s))
        {
            crc ^= b << 8;
            for (int i = 0; i < 8; i++)
            {
                crc = (crc & 0x8000) != 0 ? ((crc << 1) ^ 0x1021) & 0xFFFF : (crc << 1) & 0xFFFF;
            }
        }

        return (ushort)crc;
    }

    /// <summary>The payload with its CRC.</summary>
    public string Encode()
    {
        if (MerchantName.Length is 0 or > 25 || !Ascii(MerchantName))
        {
            throw new BrCodeException("Merchant name must be 1-25 ASCII characters.");
        }

        if (MerchantCity.Length is 0 or > 15 || !Ascii(MerchantCity))
        {
            throw new BrCodeException("Merchant city must be 1-15 ASCII characters.");
        }

        if (RecUrl.Length is 0 or > 77 || RecUrl.Contains("://", StringComparison.Ordinal))
        {
            throw new BrCodeException("Recurrence URL must be 1-77 characters without scheme.");
        }

        if (CobUrl.Length > 77 || CobUrl.Contains("://", StringComparison.Ordinal))
        {
            throw new BrCodeException("Payment URL must be at most 77 characters without scheme.");
        }

        string mai = Tlv("00", Gui) + (CobUrl.Length > 0 ? Tlv("25", CobUrl) : "");
        var b = new StringBuilder(Tlv("00", "01"));
        if (CobUrl.Length > 0)
        {
            b.Append(Tlv("01", "12"));
        }

        b.Append(Tlv("26", mai)).Append(Tlv("52", "0000")).Append(Tlv("53", "986"));
        if (Amount.Length > 0)
        {
            b.Append(Tlv("54", Amount));
        }

        b.Append(Tlv("58", "BR")).Append(Tlv("59", MerchantName)).Append(Tlv("60", MerchantCity))
            .Append(Tlv("62", Tlv("05", "***"))).Append(Tlv("80", Tlv("00", Gui) + Tlv("25", RecUrl))).Append("6304");
        string s = b.ToString();
        return s + Crc16(s).ToString("X4", CultureInfo.InvariantCulture);
    }

    /// <summary>Splits a payload into top-level fields, checking the CRC.</summary>
    public static IReadOnlyList<BrCodeField> Parse(string s)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (s.Length < 8 || s.Substring(s.Length - 8, 4) != "6304")
        {
            throw new BrCodeException("Missing CRC field.");
        }

        if (!Crc16(s[..^4]).ToString("X4", CultureInfo.InvariantCulture).Equals(s[^4..], StringComparison.OrdinalIgnoreCase))
        {
            throw new BrCodeException("CRC mismatch.");
        }

        var output = new List<BrCodeField>();
        for (int i = 0; i < s.Length;)
        {
            if (i + 4 > s.Length || !int.TryParse(s.AsSpan(i + 2, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int n) || i + 4 + n > s.Length)
            {
                throw new BrCodeException("Malformed field.");
            }

            output.Add(new BrCodeField(s.Substring(i, 2), s.Substring(i + 4, n)));
            i += 4 + n;
        }

        return output;
    }

    /// <summary>2 without a payment location, 3 with one (3 or 4: the location tells), 0 without a recurrence.</summary>
    public static int Journey(IEnumerable<BrCodeField> fields)
    {
        bool rec = false, cob = false;
        foreach (BrCodeField f in fields)
        {
            if (f.Id == "80" && f.Value.Contains(Gui, StringComparison.Ordinal))
            {
                rec = true;
            }

            if (f.Id == "26" && f.Value.Contains("25", StringComparison.Ordinal) && f.Value.Length > Tlv("00", Gui).Length)
            {
                cob = true;
            }
        }

        return !rec ? 0 : cob ? 3 : 2;
    }
}
