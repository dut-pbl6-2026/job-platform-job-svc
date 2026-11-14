namespace Job.Api.DTOs;

/// <summary>Small nested reference to another resource (api-conventions §3.1): <c>{ id, name }</c>.</summary>
public record ResourceRefDto(Guid Id, string Name);
