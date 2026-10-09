using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Boardcast.Core;
using Microsoft.Win32.SafeHandles;

namespace Boardcast.Windows;

internal static class BackendLifetime
{
    // Obtain the PID from the authenticated pipe connection, never by executable name.
    // This also works when the connected backend is hosted by SCM under this user.
    public static async Task StopAsync(string? dataDirectory, CancellationToken cancellationToken, Process? launchedBackend = null)
    {
        using var pipe = new NamedPipeClientStream(".", IpcClient.GetPipeName(dataDirectory),
            PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        while (!pipe.IsConnected)
        {
            using var connect = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connect.CancelAfter(TimeSpan.FromSeconds(1));
            try { await pipe.ConnectAsync(connect.Token); }
            catch (Exception ex) when (ex is IOException or OperationCanceledException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // A process just launched by this window may not have created its lock or pipe yet.
                if ((launchedBackend is null || launchedBackend.HasExited) && IsStopped(dataDirectory)) return;
                await Task.Delay(100, cancellationToken);
            }
        }

        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint processId))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法确认后台进程。");
        Process backend;
        try { backend = Process.GetProcessById(checked((int)processId)); }
        catch (ArgumentException) { return; }
        using (backend)
        {
            try
            {
                await WireProtocol.WriteAsync(pipe, new IpcRequest { Command = "stop" }, cancellationToken);
                var reply = await WireProtocol.ReadAsync<IpcResponse>(pipe, cancellationToken);
                if (!reply.Success) throw new IOException(reply.Error);
            }
            catch (IOException) when (backend.HasExited) { return; }
            await backend.WaitForExitAsync(cancellationToken);
        }
    }

    private static bool IsStopped(string? dataDirectory)
    {
        string directory = Path.GetFullPath(dataDirectory ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Boardcast"));
        string path = Path.Combine(directory, "daemon.lock");
        if (!File.Exists(path)) return true;
        try { using var unlocked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None); return true; }
        catch (FileNotFoundException) { return true; }
        catch (IOException) { return false; }
    }

    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
}
