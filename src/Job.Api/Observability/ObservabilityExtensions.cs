using Serilog;
using Serilog.Formatting.Compact;

namespace Job.Api.Observability;

public static class ObservabilityExtensions
{
    public const string ServiceName = "job";

    /// <summary>MAINT-06: Serilog — compact JSON in non-Development, readable console in Development.</summary>
    public static WebApplicationBuilder AddObservability(this WebApplicationBuilder builder)
    {
        builder.Host.UseSerilog((ctx, services, cfg) =>
        {
            cfg.ReadFrom.Configuration(ctx.Configuration)
                .Enrich.FromLogContext()
                .Enrich.WithProperty("ServiceName", ServiceName);

            if (ctx.HostingEnvironment.IsDevelopment())
                cfg.WriteTo.Console();
            else
                cfg.WriteTo.Console(new RenderedCompactJsonFormatter());
        });
        return builder;
    }

    /// <summary>Correlation id first so request logging and error handling both carry it.</summary>
    public static WebApplication UseObservability(this WebApplication app)
    {
        app.UseMiddleware<CorrelationIdMiddleware>();
        app.UseSerilogRequestLogging(o =>
        {
            // /health is polled by Render/Docker every few seconds — keep it out of the info logs.
            o.GetLevel = (ctx, _, ex) =>
                ex is not null || ctx.Response.StatusCode >= 500 ? Serilog.Events.LogEventLevel.Error
                : ctx.Request.Path.StartsWithSegments("/health") ? Serilog.Events.LogEventLevel.Verbose
                : Serilog.Events.LogEventLevel.Information;
        });
        return app;
    }
}
