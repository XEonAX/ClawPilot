using System.ComponentModel;
using Microsoft.SemanticKernel;

namespace ClawPilot.AI.Plugins;

public class WebPlugin
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _httpClientFactory;

    public WebPlugin(IServiceScopeFactory scopeFactory, IHttpClientFactory httpClientFactory)
    {
        _scopeFactory = scopeFactory;
        _httpClientFactory = httpClientFactory;
    }

    [KernelFunction("fetch_url")]
    [Description("Fetch content from a URL with optional HTTP method, headers, and body.")]
    public async Task<string> FetchUrlAsync(
        [Description("The URL to fetch")] string url,
        [Description("The HTTP method to use (e.g. GET, POST). Default is GET")] string method = "GET",
        [Description("Optional HTTP headers as JSON (e.g. '{\"Authorization\": \"Bearer token\"}')")] string? headersJson = null,
        [Description("Optional request body for POST/PUT requests")] string? body = null,
        CancellationToken ct = default)
    {
        try
        {
            using var httpClient = _httpClientFactory.CreateClient();
            var request = new HttpRequestMessage(new HttpMethod(method), url);
            if (!string.IsNullOrEmpty(headersJson))
            {
                var headers = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(headersJson);
                if (headers != null)
                {
                    foreach (var header in headers)
                    {
                        request.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    }
                }
            }
            if (!string.IsNullOrEmpty(body) && (method == "POST" || method == "PUT"))
            {
                request.Content = new StringContent(body);
            }
            var response = await httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            var content = await response.Content.ReadAsStringAsync(ct);
            return content.Length > 1000 ? content.Substring(0, 1000) + "..." : content;
        }
        catch (Exception ex)
        {
            return $"Error fetching URL: {ex.Message}";
        }
    }
}