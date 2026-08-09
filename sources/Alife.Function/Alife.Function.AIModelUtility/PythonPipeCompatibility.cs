using System;
using System.Threading;
using System.Threading.Tasks;

namespace Alife.Function.PythonPipe;

/// <summary>
/// Compatibility facade for plugins built against the former PythonPipe namespace.
/// </summary>
[Obsolete("Use Alife.Function.AIModelUtility.PythonPipeProcess instead.")]
public sealed class PythonPipeProcess : IAsyncDisposable
{
    public PythonPipeProcess(string scriptName, string pythonCode, string? pythonExe = null)
    {
        inner = new AIModelUtility.PythonPipeProcess(scriptName, pythonCode, pythonExe);
        inner.OnStderr += line => OnStderr?.Invoke(line);
    }

    public event Action<string>? OnStderr;

    public Task StartAsync(CancellationToken cancellationToken = default) =>
        inner.StartAsync(cancellationToken);

    public Task<T> InvokeAsync<T>(string funcName, params object[] args) =>
        inner.InvokeAsync<T>(funcName, args);

    public ValueTask DisposeAsync() => inner.DisposeAsync();

    readonly AIModelUtility.PythonPipeProcess inner;
}
