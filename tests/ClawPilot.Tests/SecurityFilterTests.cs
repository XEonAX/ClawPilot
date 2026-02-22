using ClawPilot.AI.Filters;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel;
using Xunit;

namespace ClawPilot.Tests;

public class SecurityFilterTests
{
    private static Kernel CreateKernelWithFilter()
    {
        var kernel = Kernel.CreateBuilder().Build();
        kernel.FunctionInvocationFilters.Add(new SecurityFilter(NullLogger<SecurityFilter>.Instance));
        return kernel;
    }

    [Theory]
    [InlineData("shell_exec")]
    [InlineData("run_bash")]
    [InlineData("exec_command")]
    [InlineData("run_command")]
    public async Task SecurityFilter_BlocksDangerousTools(string toolName)
    {
        var kernel = CreateKernelWithFilter();
        var called = false;
        var function = KernelFunctionFactory.CreateFromMethod(() =>
        {
            called = true;
            return "ok";
        }, toolName);

        var result = await function.InvokeAsync(kernel);

        Assert.False(called);
        Assert.Contains("blocked", result.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("rm -rf /")]
    [InlineData("sudo apt install")]
    [InlineData("chmod 777 /etc")]
    [InlineData("mkfs.ext4")]
    [InlineData("> /dev/sda")]
    public async Task SecurityFilter_BlocksDangerousArgs(string dangerousArg)
    {
        var kernel = CreateKernelWithFilter();
        var called = false;
        var function = KernelFunctionFactory.CreateFromMethod(() =>
        {
            called = true;
            return "ok";
        }, "safe_tool");

        var args = new KernelArguments { ["command"] = dangerousArg };
        var result = await function.InvokeAsync(kernel, args);

        Assert.False(called);
        Assert.Contains("blocked", result.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SecurityFilter_AllowsSafeTools()
    {
        var kernel = CreateKernelWithFilter();
        var called = false;
        var function = KernelFunctionFactory.CreateFromMethod(() =>
        {
            called = true;
            return "ok";
        }, "search_messages");

        var args = new KernelArguments { ["query"] = "hello world" };
        var result = await function.InvokeAsync(kernel, args);

        Assert.True(called);
        Assert.Equal("ok", result.ToString());
    }
}
