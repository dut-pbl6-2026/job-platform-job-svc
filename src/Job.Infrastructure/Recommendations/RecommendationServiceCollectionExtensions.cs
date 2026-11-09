using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Job.Infrastructure.Recommendations;

/// <summary>DI wiring for recommendations (PBL6-10), shared by Program and the tests.</summary>
public static class RecommendationServiceCollectionExtensions
{
    /// <summary>
    /// Registers options (validated at startup), the interaction writer and the service.
    /// The Kafka consumer and the backfill are registered by the host, depending on config.
    /// </summary>
    public static IServiceCollection AddRecommendations(this IServiceCollection services)
    {
        services.AddOptions<RecommendationOptions>()
            .Configure<IConfiguration>(RecommendationOptions.Bind)
            .ValidateOnStart();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<RecommendationOptions>, RecommendationOptionsValidator>());
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<InteractionWriter>();
        services.TryAddScoped<RecommendationService>();
        return services;
    }

    private sealed class RecommendationOptionsValidator : IValidateOptions<RecommendationOptions>
    {
        public ValidateOptionsResult Validate(string? name, RecommendationOptions options)
        {
            var errors = options.Validate();
            return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
        }
    }
}
