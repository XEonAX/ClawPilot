using Serilog;
using Serilog.Events;

namespace ClawPilot.Logging;

public static class SerilogConfig
{
    public static LoggerConfiguration CreateFromConfiguration(IConfiguration configuration)
    {
        return new LoggerConfiguration()
            .ReadFrom.Configuration(configuration)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Application", "ClawPilot");
    }
}
