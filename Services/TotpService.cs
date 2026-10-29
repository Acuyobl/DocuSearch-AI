using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace DocuSearch.Services;

/// <summary>
/// Implementación mínima de TOTP (RFC 6238) con HMAC-SHA1, 6 dígitos y pasos de 30 s.
/// Se usa como segundo factor de acceso junto al correo del usuario.
/// </summary>
public static class Totp
{
    public const int Digits = 6;
    public const int PeriodSeconds = 30;
    public const int DefaultWindow = 1;
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static long StepAt(DateTimeOffset moment) => moment.ToUnixTimeSeconds() / PeriodSeconds;

    public static string Generate(string base32Secret, DateTimeOffset moment) =>
        GenerateAt(DecodeBase32(base32Secret), StepAt(moment));

    /// <summary>Valida un código permitiendo ±<paramref name="window"/> pasos de reloj.</summary>
    public static bool TryVerify(string base32Secret, string? code, DateTimeOffset moment, int window, out long matchedStep)
    {
        matchedStep = 0;
        var normalized = NormalizeCode(code);
        if (normalized is null) return false;
        var key = DecodeBase32(base32Secret);
        var current = StepAt(moment);
        var tolerance = Math.Clamp(window, 0, 3);
        for (var delta = -tolerance; delta <= tolerance; delta++)
        {
            var step = current + delta;
            if (step < 0) continue;
            if (FixedTimeEquals(GenerateAt(key, step), normalized))
            {
                matchedStep = step;
                return true;
            }
        }
        return false;
    }

    public static string OtpAuthUri(string issuer, string account, string base32Secret)
    {
        var label = Uri.EscapeDataString($"{issuer}:{account}");
        var issuerParam = Uri.EscapeDataString(issuer);
        return $"otpauth://totp/{label}?secret={base32Secret}&issuer={issuerParam}&algorithm=SHA1&digits={Digits}&period={PeriodSeconds}";
    }

    /// <summary>Formatea el secreto en grupos de cuatro para poder teclearlo a mano.</summary>
    public static string FormatSecret(string base32Secret)
    {
        var normalized = NormalizeSecret(base32Secret);
        return string.Join(' ', Enumerable.Range(0, (normalized.Length + 3) / 4)
            .Select(i => normalized.Substring(i * 4, Math.Min(4, normalized.Length - i * 4))));
    }

    private static string? NormalizeCode(string? code)
    {
        if (code is null) return null;
        var digits = new string(code.Where(char.IsAsciiDigit).ToArray());
        return digits.Length == Digits ? digits : null;
    }

    private static string GenerateAt(byte[] key, long step)
    {
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, step);
        var hash = HMACSHA1.HashData(key, counter);
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24)
            | (hash[offset + 1] << 16)
            | (hash[offset + 2] << 8)
            | hash[offset + 3];
        var code = binary % 1_000_000;
        return code.ToString("D6", CultureInfo.InvariantCulture);
    }

    private static bool FixedTimeEquals(string expected, string actual) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(actual));

    private static string NormalizeSecret(string secret)
    {
        var builder = new StringBuilder(secret.Length);
        foreach (var character in secret.ToUpperInvariant())
        {
            if (character is ' ' or '-' or '=') continue;
            builder.Append(character);
        }
        var value = builder.ToString();
        if (value.Length == 0) throw new FormatException("El secreto TOTP está vacío.");
        if (value.Any(c => c is not (>= 'A' and <= 'Z') && c is not (>= '2' and <= '7')))
            throw new FormatException("El secreto TOTP no es Base32 válido.");
        return value;
    }

    private static byte[] DecodeBase32(string secret)
    {
        var normalized = NormalizeSecret(secret);
        var bytes = new List<byte>(normalized.Length * 5 / 8);
        var buffer = 0;
        var bits = 0;
        foreach (var character in normalized)
        {
            var value = character >= 'A' ? character - 'A' : character - '2' + 26;
            buffer = (buffer << 5) | value;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                bytes.Add((byte)((buffer >> bits) & 0xFF));
            }
        }
        return [.. bytes];
    }
}
