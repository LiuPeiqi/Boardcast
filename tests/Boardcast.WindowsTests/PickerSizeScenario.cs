using System.IO;
using System.Windows.Automation;

namespace Boardcast.WindowsTests;

internal static partial class Program
{
    private static async Task VerifyPickerSizesAsync(AutomationElement main, AutomationElement selectedItems, AutomationElement add,
        int processId, string source, string fileName, string folderName)
    {
        string folder = Path.Combine(source, folderName);
        long fileBytes = new FileInfo(Path.Combine(source, fileName)).Length;
        long folderBytes = new FileInfo(Path.Combine(folder, "nested.txt")).Length;
        await WaitUntilAsync(() => QueueItem(selectedItems, folderName)?.Current.Name is { } name &&
            name.Contains(folderBytes + " B", StringComparison.Ordinal) &&
            name.Contains("1 个文件", StringComparison.Ordinal) && !name.Contains("正在统计", StringComparison.Ordinal),
            "the folder asynchronously displays its exact byte total and file count");
        Check(QueueItem(selectedItems, fileName)?.Current.Name.Contains(fileBytes + " B", StringComparison.Ordinal) == true,
            "the standalone file displays its own byte size");
        await EditExclusionsAsync(main, selectedItems, processId, folderName, "nested.txt", save: false);
        Check(QueueItem(selectedItems, folderName)?.Current.Name is { } cancelledName &&
            cancelledName.Contains(folderBytes + " B", StringComparison.Ordinal) && !cancelledName.Contains("已排除", StringComparison.Ordinal),
            "cancelling exclusion edits preserves the folder content and size");
        await EditExclusionsAsync(main, selectedItems, processId, folderName, "nested.txt", save: true);
        await WaitUntilAsync(() => QueueItem(selectedItems, folderName)?.Current.Name is { } excludedName &&
            excludedName.Contains("0 B", StringComparison.Ordinal) && excludedName.Contains("0 个文件", StringComparison.Ordinal) &&
            excludedName.Contains("已排除 1 项", StringComparison.Ordinal),
            "excluding the nested file updates the folder to zero bytes and files");
        await EditExclusionsAsync(main, selectedItems, processId, folderName, "", save: true);
        await WaitUntilAsync(() => QueueItem(selectedItems, folderName)?.Current.Name is { } restoredName &&
            restoredName.Contains(folderBytes + " B", StringComparison.Ordinal) && restoredName.Contains("1 个文件", StringComparison.Ordinal) &&
            !restoredName.Contains("已排除", StringComparison.Ordinal),
            "clearing exclusions rescans the full folder content");
        await Task.Delay(300);
        Check(QueueItem(selectedItems, folderName)?.Current.Name is { } stableName &&
            stableName.Contains(folderBytes + " B", StringComparison.Ordinal) && !stableName.Contains("已排除", StringComparison.Ordinal),
            "earlier exclusion scans do not replace the restored folder result");
        ((SelectionItemPattern)QueueItem(selectedItems, folderName)!.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
        InvokeControl(FindById(main, "RemoveSelectedButton"), processId);
        await WaitUntilAsync(() => QueueElements(selectedItems).Length == 1 && QueueItem(selectedItems, folderName) is null &&
            QueueItem(selectedItems, fileName) is not null, "removing the folder leaves only the standalone file");
        await Task.Delay(300);
        Check(QueueElements(selectedItems).Length == 1 && QueueItem(selectedItems, folderName) is null,
            "asynchronous folder results do not reinsert the removed row");

        // Change this test's own fixture before re-adding to detect stale cached scan results.
        await File.WriteAllTextAsync(Path.Combine(folder, "nested.txt"), "The re-added folder has changed content and must be scanned again, independently of its removed row.");
        await File.WriteAllBytesAsync(Path.Combine(folder, "another.bin"), new byte[17]);
        long revisedBytes = Directory.EnumerateFiles(folder).Sum(path => new FileInfo(path).Length);
        await AddUsingNativePickerAsync(add, processId, folders: true, folder);
        await WaitUntilAsync(() => QueueElements(selectedItems).Length == 2 && QueueItem(selectedItems, folderName)?.Current.Name is { } name &&
            name.Contains(revisedBytes + " B", StringComparison.Ordinal) && name.Contains("2 个文件", StringComparison.Ordinal),
            "the re-added folder displays newly scanned size and count");
        Check(QueueItem(selectedItems, fileName) is not null &&
            QueueElements(selectedItems).Count(item => item.Current.Name.Contains(folderName, StringComparison.Ordinal)) == 1,
            "re-adding creates exactly one folder row and preserves the file row");
    }

    private static async Task EditExclusionsAsync(AutomationElement main, AutomationElement selectedItems, int processId,
        string folderName, string paths, bool save)
    {
        ((SelectionItemPattern)QueueItem(selectedItems, folderName)!.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
        InvokeControl(FindById(main, "ExcludeItemsButton"), processId);
        nint window = await WaitForOwnedWindowAsync(processId, "排除项 · " + folderName);
        var dialog = AutomationElement.FromHandle(window);
        ((ValuePattern)FindById(dialog, "ExcludedPaths").GetCurrentPattern(ValuePattern.Pattern)).SetValue(paths);
        var action = dialog.FindFirst(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
            new PropertyCondition(AutomationElement.NameProperty, save ? "确定" : "取消")))
            ?? throw new InvalidOperationException("Missing exclusion dialog action.");
        InvokeControl(action, processId);
        await WaitUntilAsync(() => !Native.IsWindowVisible(window), save ? "saving exclusion edits closes the dialog" : "cancelling exclusion edits closes the dialog");
    }
    private static AutomationElement[] QueueElements(AutomationElement parent) =>
        parent.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem))
            .Cast<AutomationElement>().ToArray();

    private static AutomationElement? QueueItem(AutomationElement parent, string name) =>
        QueueElements(parent).FirstOrDefault(item => item.Current.Name.Contains(name, StringComparison.Ordinal));
}
