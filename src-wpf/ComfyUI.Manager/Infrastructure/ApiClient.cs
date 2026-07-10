using System.Threading.Tasks;

namespace ComfyUI.Manager.Infrastructure;

public class ApiClient
{
    public ApiClient(string baseUrl) { }
    public Task<ErrorEnvelope> GetHealthAsync()
        => Task.FromResult(new ErrorEnvelope { Ok = true });
}
