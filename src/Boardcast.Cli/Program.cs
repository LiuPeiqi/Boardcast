using System.Text.Json;
using Boardcast.Core;

Console.InputEncoding = new System.Text.UTF8Encoding(false);
Console.OutputEncoding = new System.Text.UTF8Encoding(false);
var arguments = args.ToList();
string? dataDirectory = null;
var dataIndex = arguments.IndexOf("--data-dir");
if (dataIndex >= 0 && arguments.Count > dataIndex + 1)
{
    dataDirectory = arguments[dataIndex + 1]; arguments.RemoveRange(dataIndex, 2);
}
if (arguments.Count == 0 || arguments[0] is "--help" or "help")
{
    Console.WriteLine("""
Boardcast — LAN-only file sharing
  boardcast-cli status | devices | discover | stop
  boardcast-cli connect <LAN-IP> [port]
  boardcast-cli send <fingerprint> <file-or-folder> [--exclude <relative-path>]...
  boardcast-cli accept <transfer-id> [destination]
  boardcast-cli reject <transfer-id>
  boardcast-cli pause | resume | cancel <transfer-id>
  boardcast-cli settings [--file <settings.json>]
  boardcast-cli mcp
--exclude applies to the preceding folder and excludes all children of a matched directory.
All commands optionally accept --data-dir <directory>.
The daemon must already be running. Results are JSON.
MCP uses local stdio; it does not expose a network listener.
"""); return 0;
}
try
{
    if (arguments[0] == "mcp") { await McpHost.RunAsync(dataDirectory); return 0; }
    var request = new IpcRequest();
    switch (arguments[0])
    {
        case "status": case "devices": request.Command = "status"; break;
        case "discover": case "stop": request.Command = arguments[0]; break;
        case "connect":
            Require(2); request.Command = "connect"; request.Address = arguments[1];
            if (arguments.Count > 2) request.Port = int.Parse(arguments[2]);
            break;
        case "send":
            Require(3);
            var snapshot = (await IpcClient.SendAsync(new IpcRequest(), dataDirectory)).Snapshot ?? throw new IOException("后台未返回状态。");
            request.Command = "send";
            request.Peer = snapshot.Peers.SingleOrDefault(p => string.Equals(p.Fingerprint, arguments[1], StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException("未发现该指纹的设备。先 discover 或 connect。");
            request.Sources = ParseSources(arguments.Skip(2)); break;
        case "accept": case "reject":
            Require(2); request.Command = "decide"; request.TransferId = arguments[1]; request.Accept = arguments[0] == "accept";
            if (arguments.Count > 2) request.Destination = Path.GetFullPath(arguments[2]);
            break;
        case "pause": case "resume": case "cancel": Require(2); request.Command = arguments[0]; request.TransferId = arguments[1]; break;
        case "settings":
            if (arguments.Count == 3 && arguments[1] == "--file")
            {
                request.Command = "settings";
                request.Settings = JsonSerializer.Deserialize<AppSettings>(await File.ReadAllTextAsync(arguments[2]), BoardcastJson.Options) ?? throw new ArgumentException("空设置。");
            }
            else request.Command = "status";
            break;
        default: throw new ArgumentException("未知命令。使用 --help。");
    }
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    var response = await IpcClient.SendAsync(request, dataDirectory, timeout.Token);
    object result = arguments[0] == "devices" && response.Success ? response.Snapshot?.Peers ?? [] :
        arguments[0] == "settings" && request.Command == "status" && response.Success ? (object?)response.Snapshot?.Settings ?? response : response;
    Console.WriteLine(JsonSerializer.Serialize(result, BoardcastJson.Options));
    return response.Success ? 0 : 1;
}
catch (Exception ex)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(new { success = false, error = ex.Message }, BoardcastJson.Options)); return 2;
}
void Require(int count) { if (arguments.Count < count) throw new ArgumentException("参数不足。使用 --help。"); }

static List<TransferSource> ParseSources(IEnumerable<string> values)
{
    var sources = new List<TransferSource>();
    using var arguments = values.GetEnumerator();
    while (arguments.MoveNext())
    {
        if (arguments.Current == "--exclude")
        {
            if (sources.Count == 0 || !arguments.MoveNext()) throw new ArgumentException("--exclude 需要先指定文件夹，再指定要排除的相对路径。");
            sources[^1].ExcludedPaths.AddRange(TransferExclusions.NormalizeRelativePaths([arguments.Current]));
        }
        else sources.Add(new TransferSource { Path = Path.GetFullPath(arguments.Current) });
    }
    if (sources.Count == 0) throw new ArgumentException("请选择文件或文件夹。");
    return sources;
}
