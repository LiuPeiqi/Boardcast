using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
namespace Boardcast.Core;
public static class IpcClient
{
    public static string GetPipeName(string? dataDirectory = null)
    {
        var directory = Path.GetFullPath(dataDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Boardcast"));
        directory = Path.TrimEndingDirectorySeparator(directory);
        if (OperatingSystem.IsWindows()) directory = directory.ToUpperInvariant();
        return "boardcast-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(directory)))[..24].ToLowerInvariant();
    }
    public static async Task<IpcResponse> SendAsync(IpcRequest request, string? dataDirectory = null, CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var pipe = new NamedPipeClientStream(".", GetPipeName(dataDirectory), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false);
        await WireProtocol.WriteAsync(pipe, request, deadline.Token).ConfigureAwait(false);
        return await WireProtocol.ReadAsync<IpcResponse>(pipe, deadline.Token).ConfigureAwait(false);
    }
}
