using System.Threading.RateLimiting;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

builder.Services.AddApplicationInsightsTelemetryWorkerService();

builder.Services.AddSingleton<JwtValidator>();

builder.Services.AddSingleton<PartitionedRateLimiter<string>>(_ =>
    PartitionedRateLimiter.Create<string, string>(sub =>
        RateLimitPartition.GetFixedWindowLimiter(
            sub,
            _ => new FixedWindowRateLimiterOptions {
                PermitLimit = int.TryParse(Environment.GetEnvironmentVariable("RATE_LIMIT_PER_MINUTE"), out var x) && x > 0 ? x : 30,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                AutoReplenishment = true
            }
        )
    )
);

// ✅ Name the HttpClient ("openai")
builder.Services.AddHttpClient("openai", hc =>
{
    var apiBase = Environment.GetEnvironmentVariable("OPENAI_API_BASE")?.TrimEnd('/')
                  ?? "https://api.openai.com";
    hc.BaseAddress = new Uri(apiBase);
    hc.Timeout = TimeSpan.FromSeconds(60);
});

// ❌ Remove the old Azure client block:
// builder.Services.AddHttpClient("azure-openai", ...);

var app = builder.Build();
app.Run();
