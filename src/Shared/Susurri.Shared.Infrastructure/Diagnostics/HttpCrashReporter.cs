using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Susurri.Shared.Abstractions.Diagnostics;

namespace Susurri.Shared.Infrastructure.Diagnostics;

public sealed class HttpCrashReporter : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly Uri _endpoint;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public HttpCrashReporter(Uri endpoint, HttpClient? http = null)
    {
        _endpoint = endpoint;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        _ownsHttp = http is null;
    }

    public async Task PostAsync(FatalErrorReport report, CancellationToken ct = default)
    {
        try
        {
            var json = JsonSerializer.Serialize(report, JsonOptions);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(_endpoint, content, ct).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        if (_ownsHttp)
            _http.Dispose();
    }
}
