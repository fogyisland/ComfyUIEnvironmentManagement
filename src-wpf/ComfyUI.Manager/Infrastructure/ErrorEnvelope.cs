namespace ComfyUI.Manager.Infrastructure;

public class ErrorEnvelope
{
    public bool Ok { get; set; }
    public object? Value { get; set; }
    public ErrorBody? Error { get; set; }
}
public class ErrorBody
{
    public string Code { get; set; } = "";
    public string Message { get; set; } = "";
}
