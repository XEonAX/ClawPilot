using System.Threading.Channels;
using ClawPilot.AI;
using ClawPilot.Channels;
using ClawPilot.Configuration;
using ClawPilot.Database;
using ClawPilot.Logging;
using ClawPilot.Services;
using ClawPilot.Skills;
using Microsoft.EntityFrameworkCore;
using Serilog;

var builder = Host.CreateApplicationBuilder(args);

Log.Logger = SerilogConfig.CreateFromConfiguration(builder.Configuration).CreateLogger();
builder.Services.AddSerilog();

builder.Services.Configure<ClawPilotOptions>(builder.Configuration.GetSection(ClawPilotOptions.SectionName));
var config = builder.Configuration.GetSection(ClawPilotOptions.SectionName).Get<ClawPilotOptions>()
    ?? throw new InvalidOperationException("ClawPilot configuration section is missing or invalid.");

builder.Services.AddDbContext<ClawPilotDbContext>(o =>
    o.UseSqlite($"Data Source={config.DatabasePath}"));

builder.Services.AddSingleton(Channel.CreateUnbounded<IncomingMessage>(new UnboundedChannelOptions
{
    SingleReader = true,
}));

builder.Services.AddSingleton<ITelegramChannel, TelegramChannel>();
builder.Services.AddSingleton<MemoryService>();
builder.Services.AddSingleton<GroupQueueService>();
builder.Services.AddSingleton(sp =>
{
    var skillsDir = Path.Combine(AppContext.BaseDirectory, "skills");
    var loader = new SkillLoaderService(
        skillsDir,
        sp.GetRequiredService<IServiceScopeFactory>(),
        sp.GetRequiredService<ILoggerFactory>().CreateLogger<SkillLoaderService>());
    loader.LoadAll();
    return loader;
});

// §2.2: AgentOrchestrator builds its own Kernel internally
builder.Services.AddSingleton<AgentOrchestrator>();

builder.Services.AddHostedService<TelegramHostedService>();
builder.Services.AddHostedService<MessageProcessorService>();
builder.Services.AddHostedService<TaskSchedulerService>();
builder.Services.AddHostedService<HealthCheckService>();

var host = builder.Build();

using (var scope = host.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ClawPilotDbContext>();
    await db.Database.MigrateAsync();
}

await host.RunAsync();
