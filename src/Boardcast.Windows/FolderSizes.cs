using Boardcast.Core;
namespace Boardcast.Windows;
public partial class MainWindow
{
    private readonly FolderSizeScanner _folderSizes = new();
    private readonly HashSet<Task> _sizeTasks = [];
    private async Task UpdateFolderSizeAsync(SelectedPath item)
    {
        var scanToken = item.CancellationToken;
        var excludedPaths = item.ExcludedPaths.ToArray();
        try
        {
            var result = await _folderSizes.ScanAsync(item.Path, excludedPaths, scanToken);
            if (item.IsRemoved || !_paths.Contains(item) || scanToken.IsCancellationRequested || scanToken != item.CancellationToken) return;
            item.SizeText = result.IsComplete
                ? $"{Size(result.Bytes)} · {result.FileCount:N0} 个文件"
                : $"至少 {Size(result.Bytes)} · {result.SkippedCount:N0} 项未统计";
        }
        catch (OperationCanceledException)
        {
            if (!item.IsRemoved && _paths.Contains(item) && scanToken == item.CancellationToken) item.SizeText = "统计已取消";
        }
        catch (Exception ex)
        {
            if (!item.IsRemoved && _paths.Contains(item) && scanToken == item.CancellationToken) item.SizeText = "无法统计：" + ex.Message;
        }
    }
    private void StartFolderSize(SelectedPath item)
    {
        var task = UpdateFolderSizeAsync(item);
        _sizeTasks.Add(task);
        _ = ForgetSizeTaskAsync(task);
    }
    private async Task ForgetSizeTaskAsync(Task task)
    {
        try { await task; }
        finally { _sizeTasks.Remove(task); }
    }
    private void ClearSelectedPaths()
    {
        foreach (var item in _paths) item.Dispose();
        _paths.Clear();
    }
}
