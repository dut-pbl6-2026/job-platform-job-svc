using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Job.Infrastructure.Recommendations;

/// <summary>DI wiring for recommendations (PBL6-10), shared by Program and the tests.</summary>
public static class RecommendationServiceCollectionExtensions
{
    /// <summary>
    /// Registers options (validated at startup), the interaction writer and the service.
    /// The Kafka consumer and the backfill are registered by the host, depending on config.
    /// </summary>
    public static IServiceCollection AddRecommendations(this IServiceCollection services, IConfiguration? configuration = null)
    {
        services.AddOptions<RecommendationOptions>()
            .Configure<IConfiguration>(RecommendationOptions.Bind)
            .ValidateOnStart();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<RecommendationOptions>, RecommendationOptionsValidator>());
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<RecommendationCache>();
        services.TryAddScoped<InteractionWriter>();
        services.TryAddScoped<RecommendationService>();

        var redisUrl = configuration?["REDIS_URL"];
        if (!string.IsNullOrWhiteSpace(redisUrl))
        {
            services.TryAddSingleton<IConnectionMultiplexer>(_ =>
            {
                var options = ConfigurationOptions.Parse(redisUrl);
                options.AbortOnConnectFail = false;
                options.BacklogPolicy = BacklogPolicy.FailFast;
                return ConnectionMultiplexer.Connect(options);
            });
        }
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
