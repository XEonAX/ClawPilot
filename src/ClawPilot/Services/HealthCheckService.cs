using ClawPilot.Channels;
using ClawPilot.Configuration;
using ClawPilot.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Telegram.Bot;

namespace ClawPilot.Services;

public class HealthCheckService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ITelegramChannel _telegram;
    private readonly ClawPilotOptions _options;
    private readonly ILogger<HealthCheckService> _logger;
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    public HealthCheckService(
        IServiceScopeFactory scopeFactory,
        ITelegramChannel telegram,
        IOptions<ClawPilotOptions> options,
        ILogger<HealthCheckService> logger)
    {
        _scopeFactory = scopeFactory;
        _telegram = telegram;
        _options = options.Value;
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

    internal async Task RunChecksAsync(CancellationToken ct)
    {
        var healthy = true;

        // Check 1: Database connectivity
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

        // Check 2: Telegram bot token validity (§2.6)
        try
        {
            var bot = new TelegramBotClient(_options.TelegramBotToken);
            var me = await bot.GetMe(ct);
            _logger.LogDebug("Health check: Telegram OK (bot: @{Username})", me.Username);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Health check: Telegram FAILED");
            healthy = false;
        }

        // Check 3: OpenRouter API key validity (§2.6)
        try
        {
            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_options.OpenRouterApiKey}");
            var response = await httpClient.GetAsync("https://openrouter.ai/api/v1/models?limit=1", ct);
            response.EnsureSuccessStatusCode();
            _logger.LogDebug("Health check: OpenRouter OK");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Health check: OpenRouter FAILED");
            healthy = false;
        }

        if (healthy)
            _logger.LogInformation("Health check: all systems operational");
        else
            _logger.LogWarning("Health check: degraded state detected");
    }
}
