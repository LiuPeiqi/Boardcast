using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Boardcast.Core;

/// <summary>Explicit local invocation only. This is an integration adapter, not a sandbox.</summary>
public static class IntegrationRunner
{
    private const int OutputLimit = 16 * 1024;

    public static Task<IntegrationResult> RunAsync(IntegrationProfile profile, TransferOffer manifest, CancellationToken cancellationToken = default) =>
        RunInternalAsync(profile, manifest, null, cancellationToken);

    public static Task<IntegrationResult> RunAsync(IntegrationProfile profile, TransferOffer manifest, IReadOnlyDictionary<string, string> localPaths, CancellationToken cancellationToken = default) =>
        RunInternalAsync(profile, manifest, localPaths, cancellationToken);

    private static async Task<IntegrationResult> RunInternalAsync(IntegrationProfile profile, TransferOffer manifest, IReadOnlyDictionary<string, string>? localPaths, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(manifest);
        if (!profile.Enabled) return new(false, "", "此本机扩展未启用。");
        if (string.IsNullOrWhiteSpace(profile.Executable) || !Path.IsPathFullyQualified(profile.Executable) ||
            profile.Executable.StartsWith("\\\\", StringComparison.Ordinal) || profile.Executable.StartsWith("//", StringComparison.Ordinal) ||
            !File.Exists(profile.Executable))
            return new(false, "", "扩展必须指定已存在的可执行文件绝对路径。");
        if (OperatingSystem.IsWindows() && !string.Equals(Path.GetExtension(profile.Executable), ".exe", StringComparison.OrdinalIgnoreCase))
            return new(false, "", "Windows 扩展必须是可执行 .exe 文件；不直接运行脚本。");
        if (profile.TimeoutSeconds is < 1 or > 300)
            return new(false, "", "扩展超时必须介于 1 与 300 秒。");
        if (profile.Arguments is null || profile.Arguments.Count > 128 || profile.Arguments.Any(a => a is null || a.Length > 8192 || a.Contains('\0')))
            return new(false, "", "扩展参数无效。");
        if (manifest.Entries is null || manifest.Entries.Count > 10000 || manifest.Entries.Any(e => e is null || e.RelativePath is null) ||
            manifest.Entries.Sum(e => (long)e.RelativePath.Length) > 256 * 1024)
            return new(false, "", "清单过大，无法交给摘要扩展。");

        byte[] input;
        try
        {
            PathSafety.ValidateManifest(manifest, new AppSettings { MaxTransferBytes = long.MaxValue });
            var packet = new Dictionary<string, object>
            {
                ["schema"] = "boardcast.integration-metadata.v1",
                ["untrustedMetadata"] = true,
                ["transferId"] = manifest.Id,
                ["senderName"] = manifest.SenderName,
                ["totalBytes"] = manifest.TotalBytes,
                ["entries"] = manifest.Entries.Select(e => new { relativePath = e.RelativePath, size = e.Size, isDirectory = e.IsDirectory }).ToArray()
            };
            if (profile.IncludeLocalPaths)
            {
                if (localPaths is null) return new(false, "", "此扩展要求本机选中文件映射；未提供本机文件。");
                var files = manifest.Entries.Where(e => !e.IsDirectory).ToArray();
                if (localPaths.Count != files.Length) return new(false, "", "本机文件映射必须与清单文件完全对应。");
                var selected = new List<object>();
                foreach (var entry in files)
                {
                    if (!localPaths.TryGetValue(entry.RelativePath, out var localPath) ||
                        !Path.IsPathFullyQualified(localPath) || localPath.StartsWith("\\\\", StringComparison.Ordinal) ||
                        localPath.StartsWith("//", StringComparison.Ordinal) || !File.Exists(localPath))
                        return new(false, "", "扩展只允许访问本机明确选择且仍然存在的文件。");
                    selected.Add(new { relativePath = entry.RelativePath, localPath = Path.GetFullPath(localPath) });
                }
                packet["localFiles"] = selected;
            }
            input = JsonSerializer.SerializeToUtf8Bytes(packet, BoardcastJson.Options);
            if (input.Length > 1024 * 1024) return new(false, "", "清单超过扩展输入限制。");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or OverflowException)
        { return new(false, "", ex.Message); }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(profile.TimeoutSeconds));
        var start = new ProcessStartInfo(profile.Executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(profile.Executable)!
        };
        foreach (var argument in profile.Arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!process.Start()) return new(false, "", "扩展无法启动。");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or OperationCanceledException)
        { return new(false, "", ex is OperationCanceledException ? "扩展已取消。" : ex.Message); }

        var outputTask = ReadBoundedAsync(process.StandardOutput.BaseStream, timeout.Token);
        var errorTask = ReadBoundedAsync(process.StandardError.BaseStream, timeout.Token);
        string failure = "";
        try
        {
            await process.StandardInput.BaseStream.WriteAsync(input, timeout.Token).ConfigureAwait(false);
            await process.StandardInput.BaseStream.FlushAsync(timeout.Token).ConfigureAwait(false);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await Task.WhenAll(outputTask, errorTask).WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            failure = cancellationToken.IsCancellationRequested ? "扩展已取消。" : "扩展超时，已请求终止进程树。";
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        { failure = ex.Message; }
        finally
        {
            if (failure.Length > 0)
            {
                timeout.Cancel();
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException) { }
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
                catch (Exception ex) when (ex is TimeoutException or InvalidOperationException) { }
            }
        }

        var output = Encoding.UTF8.GetString(await outputTask.ConfigureAwait(false));
        var error = Encoding.UTF8.GetString(await errorTask.ConfigureAwait(false));
        if (failure.Length > 0) error = failure + (error.Length == 0 ? "" : "\n" + error);
        var success = failure.Length == 0 && process.HasExited && process.ExitCode == 0;
        if (!success && error.Length == 0) error = "扩展退出码：" + (process.HasExited ? process.ExitCode : -1);
        // Limit the final UTF-8 representation, including replacement characters for malformed output.
        error = LimitUtf8(error, OutputLimit);
        output = LimitUtf8(output, OutputLimit - Encoding.UTF8.GetByteCount(error));
        return new(success, output, error);
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var retained = new MemoryStream(OutputLimit);
        var buffer = new byte[4096];
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                var keep = Math.Min(read, OutputLimit - (int)retained.Length);
                if (keep > 0) retained.Write(buffer, 0, keep);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (IOException) { }
        return retained.ToArray();
    }

    private static string LimitUtf8(string value, int maximum)
    {
        var count = 0;
        var builder = new StringBuilder(Math.Min(value.Length, maximum));
        foreach (var rune in value.EnumerateRunes())
        {
            if (count + rune.Utf8SequenceLength > maximum) break;
            builder.Append(rune.ToString());
            count += rune.Utf8SequenceLength;
        }
        return builder.ToString();
    }
}
