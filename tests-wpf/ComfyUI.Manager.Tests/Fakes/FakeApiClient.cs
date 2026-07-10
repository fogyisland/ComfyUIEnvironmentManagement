using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Infrastructure;

namespace ComfyUI.Manager.Tests.Fakes;

public class FakeApiClient : ApiClient
{
    public ConcurrentDictionary<string, Func<object?, object>> Handlers { get; } = new();

    public FakeApiClient() : base("http://fake:7800") { }

    public void Register(string route, Func<object?, object> handler)
    {
        Handlers[route] = handler;
    }

    public override Task<ErrorEnvelope<T>> PostAsync<T>(
        string route, object body, CancellationToken ct = default)
    {
        if (Handlers.TryGetValue(route, out var h))
        {
            var result = h(body);
            return Task.FromResult(new ErrorEnvelope<T>
            {
                Ok = true,
                Value = (T)result,
            });
        }
        return Task.FromResult(new ErrorEnvelope<T>
        {
            Ok = false,
            Error = new ErrorBody { Code = "ROUTE_NOT_FOUND", Message = route },
        });
    }

    public override Task<ErrorEnvelope<T>> GetAsync<T>(
        string route, CancellationToken ct = default)
        => PostAsync<T>(route, new { });
}