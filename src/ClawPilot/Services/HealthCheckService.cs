using ClawPilot.Channels;
using ClawPilot.Database;
using Microsoft.EntityFrameworkCore;

namespace ClawPilot.Services;

public class HealthCheckService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<HealthCheckService> _logger;
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    public HealthCheckService(
        IServiceScopeFactory scopeFactory,
        ILogger<HealthCheckService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("HealthCheckService started");

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(Interval, stoppingToken);
            await RunChecksAsync(stoppingToken);
        }
    }

    private async Task RunChecksAsync(CancellationToken ct)
    {
        var healthy = true;

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ClawPilotDbContext>();
            await db.Database.ExecuteSqlRawAsync("SELECT 1", ct);
            _logger.LogDebug("Health check: database OK");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Health check: database FAILED");
            healthy = false;
        }

        if (healthy)
            _logger.LogInformation("Health check: all systems operational");
        else
            _logger.LogWarning("Health check: degraded state detected");
    }
}
