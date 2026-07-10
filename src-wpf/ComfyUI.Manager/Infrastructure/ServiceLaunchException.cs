using System;

namespace ComfyUI.Manager.Infrastructure;

public class ServiceLaunchException : Exception
{
    public ServiceLaunchException(string message) : base(message) { }
    public ServiceLaunchException(string message, Exception inner)
        : base(message, inner) { }
}
