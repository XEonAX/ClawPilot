using Microsoft.SemanticKernel;

namespace ClawPilot.AI.Filters;

public class SecurityFilter : IFunctionInvocationFilter
{
    private static readonly string[] BlockedToolPatterns = ["shell", "bash", "exec", "run_command"];
    private static readonly string[] DangerousArgPatterns = ["rm -rf", "sudo", "chmod 777", "mkfs", "> /dev/"];

    private readonly ILogger<SecurityFilter> _logger;

    public SecurityFilter(ILogger<SecurityFilter> logger)
    {
        _logger = logger;
    }

    public async Task OnFunctionInvocationAsync(FunctionInvocationContext context, Func<FunctionInvocationContext, Task> next)
    {
        var functionName = context.Function.Name;

        _logger.LogDebug("SecurityFilter: pre-invocation check for {Plugin}.{Function}",
            context.Function.PluginName, functionName);

        if (IsBlockedTool(functionName))
        {
            _logger.LogWarning("SecurityFilter blocked tool: {Function}", functionName);
            context.Result = new FunctionResult(context.Function, "Tool blocked by security policy.");
            return;
        }

        if (HasDangerousArgs(context.Arguments))
        {
            _logger.LogWarning("SecurityFilter blocked dangerous arguments for tool: {Function}", functionName);
            context.Result = new FunctionResult(context.Function, "Arguments blocked by security policy.");
            return;
        }

        await next(context);

        _logger.LogDebug("SecurityFilter: post-invocation for {Plugin}.{Function}",
            context.Function.PluginName, functionName);
    }

    private static bool IsBlockedTool(string functionName)
    {
        var lower = functionName.ToLowerInvariant();
        return Array.Exists(BlockedToolPatterns, pattern => lower.Contains(pattern, StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasDangerousArgs(KernelArguments? arguments)
    {
        if (arguments is null) return false;

        foreach (var (_, value) in arguments)
        {
            if (value is not string strValue) continue;
            if (Array.Exists(DangerousArgPatterns, pattern =>
                strValue.Contains(pattern, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }
}
