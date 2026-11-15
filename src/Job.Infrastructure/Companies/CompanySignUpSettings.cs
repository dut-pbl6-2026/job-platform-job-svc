using Microsoft.Extensions.Configuration;

namespace Job.Infrastructure.Companies;

/// <summary>
/// Configuration for company self-serve sign-up (SRS AUTH-01-11 to AUTH-01-13).
/// Values come from the environment only; nothing is hard-coded per environment.
/// </summary>
public static class CompanySignUpSettings
{
    /// <summary>Waiting period of a join request in days (AUTH-01-13, backlog A11).</summary>
    public const string JoinRequestTtlDaysKey = "COMPANY_JOIN_REQUEST_TTL_DAYS";

    /// <summary>Extra blocked email domains, comma separated, added to the built-in list.</summary>
    public const string ExtraEmailBlocklistKey = "COMPANY_EMAIL_DOMAIN_BLOCKLIST";

    public const int DefaultJoinRequestTtlDays = 7;

    public static TimeSpan ReadJoinRequestWaitingPeriod(IConfiguration config)
    {
        var raw = config[JoinRequestTtlDaysKey];
        return int.TryParse(raw, out var days) && days > 0
            ? TimeSpan.FromDays(days)
            : TimeSpan.FromDays(DefaultJoinRequestTtlDays);
    }

    public static IReadOnlyList<string> ReadExtraBlocklist(IConfiguration config)
    {
        var raw = config[ExtraEmailBlocklistKey];
        if (string.IsNullOrWhiteSpace(raw))
            return [];
        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(d => d.ToLowerInvariant())
            .ToList();
    }
}
