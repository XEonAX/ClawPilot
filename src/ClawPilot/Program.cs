using System.Threading.Channels;
using ClawPilot.AI;
using ClawPilot.AI.Filters;
using ClawPilot.AI.Plugins;
using ClawPilot.Channels;
using ClawPilot.Configuration;
using ClawPilot.Database;
using ClawPilot.Logging;
using ClawPilot.Services;
using ClawPilot.Skills;
using Microsoft.EntityFrameworkCore;
using Microsoft.SemanticKernel;
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
builder.Services.AddSingleton<AgentOrchestrator>();
builder.Services.AddSingleton<GroupQueueService>();
builder.Services.AddSingleton(sp =>
{
    var skillsDir = Path.Combine(AppContext.BaseDirectory, "skills");
    var loader = new SkillLoaderService(skillsDir,
        sp.GetRequiredService<ILoggerFactory>().CreateLogger<SkillLoaderService>());
    loader.LoadAll();
    return loader;
});

builder.Services.AddSingleton(sp =>
{
    var kernelBuilder = Kernel.CreateBuilder();
    kernelBuilder.AddOpenAIChatCompletion(
        modelId: config.Model,
        apiKey: config.OpenRouterApiKey,
        httpClient: new HttpClient { BaseAddress = new Uri("https://openrouter.ai/api/v1") });

    kernelBuilder.Plugins.AddFromObject(
        new MessagingPlugin(sp.GetRequiredService<ITelegramChannel>(), sp.GetRequiredService<IServiceScopeFactory>()));
    kernelBuilder.Plugins.AddFromObject(
        new SchedulerPlugin(sp.GetRequiredService<IServiceScopeFactory>()));
    kernelBuilder.Plugins.AddFromObject(
        new UtilityPlugin(sp.GetRequiredService<MemoryService>()));

    var kernel = kernelBuilder.Build();
    kernel.FunctionInvocationFilters.Add(
        new SecurityFilter(sp.GetRequiredService<ILoggerFactory>().CreateLogger<SecurityFilter>()));
    return kernel;
});

builder.Services.AddHostedService<TelegramHostedService>();
builder.Services.AddHostedService<MessageProcessorService>();
builder.Services.AddHostedService<TaskSchedulerService>();
builder.Services.AddHostedService<HealthCheckService>();

var host = builder.Build();

using (var scope = host.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ClawPilotDbContext>();
    await db.Database.EnsureCreatedAsync();
}

await host.RunAsync();
