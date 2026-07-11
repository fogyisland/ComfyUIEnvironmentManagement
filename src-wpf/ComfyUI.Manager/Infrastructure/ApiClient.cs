using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ComfyUI.Manager.Infrastructure;

public class ApiClient
{
    private readonly HttpClient _http;

    public string BaseUrl { get; }

    public ApiClient(string baseUrl)
    {
        BaseUrl = baseUrl;
        _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30),
            BaseAddress = new Uri(baseUrl + "/api/v1/"),
        };
    }

    public virtual async Task<ErrorEnvelope<T>> PostAsync<T>(
        string route, object body, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync(
            route, body, JsonOptions.Default, ct);
        var env = await resp.Content.ReadFromJsonAsync<ErrorEnvelope<T>>(
            JsonOptions.Default, ct);
        return env ?? new ErrorEnvelope<T>
        {
            Ok = false,
            Error = new ErrorBody { Code = "INTERNAL", Message = "空响应" },
        };
    }

    public virtual async Task<ErrorEnvelope<T>> GetAsync<T>(
        string route, CancellationToken ct = default)
    {
        var resp = await _http.GetAsync(route, ct);
        var env = await resp.Content.ReadFromJsonAsync<ErrorEnvelope<T>>(
            JsonOptions.Default, ct);
        return env ?? new ErrorEnvelope<T>
        {
            Ok = false,
            Error = new ErrorBody { Code = "INTERNAL", Message = "空响应" },
        };
    }

    public async Task<ErrorEnvelope> HealthAsync(CancellationToken ct = default)
    {
        var resp = await _http.GetAsync(
            new Uri(_http.BaseAddress!, "/../healthz"), ct);
        return new ErrorEnvelope { Ok = resp.IsSuccessStatusCode };
    }
}
