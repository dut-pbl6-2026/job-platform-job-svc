namespace Job.Api.DTOs;

/// <summary>Generic paginated response per SRS pagination spec (SEARCH-01-04).</summary>
public record PaginatedResponse<T>(
    IReadOnlyList<T> Items,
    int Total,
    int Page,
    int Size,
    int TotalPages);
