using Confluent.Kafka;
using Microsoft.Extensions.Options;
using SharedKernel.Kafka;

namespace Job.Api.Services;

/// <summary>
/// Background service that periodically checks Kafka broker reachability
/// when the circuit-breaker is open (plan 2.3 Recovery). Pings the broker
/// every 15s; when reachable, calls <see cref="KafkaJobEventPublisher.MarkKafkaHealthy"/>.
///
/// Does nothing when Kafka is healthy (no wasted resources) or when
/// Kafka is not configured (KAFKA_BOOTSTRAP_SERVERS empty).
/// </summary>
public sealed class KafkaHealthCheckService : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(15);

    private readonly KafkaJobEventPublisher _publisher;
    private readonly KafkaOptions _options;
    private readonly ILogger<KafkaHealthCheckService> _logger;

    public KafkaHealthCheckService(
        KafkaJobEventPublisher publisher,
        IOptions<KafkaOptions> options,
        ILogger<KafkaHealthCheckService> logger)
    {
        _publisher = publisher;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_options.BootstrapServers))
        {
            _logger.LogInformation(
                "Kafka not configured (KAFKA_BOOTSTRAP_SERVERS empty). Health-check idle.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(CheckInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            // Only check when the circuit is open (Kafka unhealthy)
            if (_publisher.IsKafkaHealthy)
            {
                continue;
            }

            if (IsBrokerReachable())
            {
                _publisher.MarkKafkaHealthy();
            }
            else
            {
                _logger.LogDebug(
                    "Kafka broker still unreachable at {Servers}. Will retry in {Interval}s.",
                    _options.BootstrapServers, CheckInterval.TotalSeconds);
            }
        }
    }

    private bool IsBrokerReachable()
    {
        try
        {
            using var adminClient = new AdminClientBuilder(
                new AdminClientConfig { BootstrapServers = _options.BootstrapServers })
                .Build();
            // GetMetadata is a synchronous call that attempts to reach the broker.
            var metadata = adminClient.GetMetadata(TimeSpan.FromSeconds(5));
            return metadata.Brokers.Count > 0;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Kafka health-check probe failed.");
            return false;
        }
    }
}
