using System.Collections.Concurrent;
using System.IO.Pipes;
namespace Boardcast.Core;
public static class IpcServer
{
    public static async Task RunAsync(string dataDirectory, Func<IpcRequest, Task<IpcResponse>> handler, CancellationToken cancellationToken)
    {
        using var slots = new SemaphoreSlim(8, 8);
        var clients = new ConcurrentDictionary<long, Task>();
        long sequence = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await slots.WaitAsync(cancellationToken).ConfigureAwait(false);
                var pipe = new NamedPipeServerStream(IpcClient.GetPipeName(dataDirectory), PipeDirection.InOut, 8, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                try { await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false); }
                catch { pipe.Dispose(); slots.Release(); throw; }
                var id = Interlocked.Increment(ref sequence);
                var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                clients[id] = done.Task;
                _ = CompleteClientAsync(pipe, handler, cancellationToken, () =>
                {
                    slots.Release();
                    clients.TryRemove(id, out _);
                    done.TrySetResult();
                });
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally { await Task.WhenAll(clients.Values).ConfigureAwait(false); }
    }
    private static async Task CompleteClientAsync(NamedPipeServerStream pipe, Func<IpcRequest, Task<IpcResponse>> handler, CancellationToken cancellationToken, Action finished)
    {
        try { await HandleAsync(pipe, handler, cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
        finally { finished(); }
    }
    private static async Task HandleAsync(NamedPipeServerStream pipe, Func<IpcRequest, Task<IpcResponse>> handler, CancellationToken cancellationToken)
    {
        using (pipe)
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            deadline.CancelAfter(TimeSpan.FromSeconds(20));
            try
            {
                IpcResponse response;
                try
                {
                    var request = await WireProtocol.ReadAsync<IpcRequest>(pipe, deadline.Token).ConfigureAwait(false);
                    response = await handler(request).WaitAsync(deadline.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    response = new IpcResponse { Success = false, Error = ex.Message };
                }
                await WireProtocol.WriteAsync(pipe, response, deadline.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
        }
    }
}
