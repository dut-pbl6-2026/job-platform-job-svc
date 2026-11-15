using System.Text.RegularExpressions;

namespace Job.Core.Companies;

/// <summary>
/// Work-email domain rules for company sign-up (SRS AUTH-01-11, AUTH-01-12).
/// A blocklisted free provider is never stored as <c>email_domain</c> and never matched to a Company.
/// </summary>
public static partial class WorkEmailDomain
{
    /// <summary>Free public providers from AUTH-01-11. Extra entries come from configuration.</summary>
    public static readonly IReadOnlyList<string> DefaultBlocklist =
    [
        "gmail.com",
        "yahoo.com",
        "outlook.com",
        "hotmail.com",
        "live.com",
        "icloud.com",
        "aol.com",
        "proton.me",
    ];

    /// <summary>
    /// Trims and lower-cases a domain and checks its shape (labels of letters, digits and hyphens,
    /// at least one dot). Returns null when the value is not a plausible domain.
    /// </summary>
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        var domain = raw.Trim().TrimEnd('.').ToLowerInvariant();
        if (domain.Length > Entities.Company.EmailDomainMaxLength)
            return null;
        return DomainPattern().IsMatch(domain) ? domain : null;
    }

    /// <summary>Extracts and normalises the domain part of an email address, or null when malformed.</summary>
    public static string? FromEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
            return null;
        var at = email.LastIndexOf('@');
        return at < 0 ? null : Normalize(email[(at + 1)..]);
    }

    /// <summary>True when the domain is on the default or configured blocklist.</summary>
    public static bool IsBlocked(string normalizedDomain, IEnumerable<string>? extraBlocklist = null)
    {
        if (DefaultBlocklist.Contains(normalizedDomain, StringComparer.OrdinalIgnoreCase))
            return true;
        return extraBlocklist is not null &&
               extraBlocklist.Any(d => string.Equals(d.Trim(), normalizedDomain, StringComparison.OrdinalIgnoreCase));
    }

    [GeneratedRegex("^(?=.{1,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\\.)+[a-z]{2,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex DomainPattern();
}
