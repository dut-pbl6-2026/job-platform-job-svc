using System.Security.Cryptography;
using System.Text;

namespace Job.Core.InviteCodes;

/// <summary>
/// Recruiter invite code format (SRS AUTH-01-09).
/// 12 characters from an alphabet without 0, O, 1 and I, shown as XXXX-XXXX-XXXX.
/// Only the SHA-256 hash of the normalised code is stored.
/// </summary>
public static class InviteCodeFormat
{
    public const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    public const int CodeLength = 12;

    /// <summary>Generates a code with a cryptographic random generator. The caller must not log it.</summary>
    public static string Generate()
    {
        Span<char> chars = stackalloc char[CodeLength];
        for (var i = 0; i < chars.Length; i++)
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return new string(chars);
    }

    /// <summary>Formats a normalised code as XXXX-XXXX-XXXX.</summary>
    public static string Display(string normalized) =>
        $"{normalized[..4]}-{normalized[4..8]}-{normalized[8..]}";

    /// <summary>
    /// Trims, uppercases and removes dashes and spaces. Returns null when the result is not
    /// exactly 12 characters from the alphabet, so every malformed input follows the same
    /// invalid-code path.
    /// </summary>
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var builder = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            if (ch == '-' || char.IsWhiteSpace(ch))
                continue;
            builder.Append(char.ToUpperInvariant(ch));
        }

        var normalized = builder.ToString();
        if (normalized.Length != CodeLength)
            return null;
        foreach (var ch in normalized)
        {
            if (Alphabet.IndexOf(ch, StringComparison.Ordinal) < 0)
                return null;
        }
        return normalized;
    }

    /// <summary>SHA-256 of the normalised code, lower-case hex (64 characters).</summary>
    public static string Hash(string normalized) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
}
