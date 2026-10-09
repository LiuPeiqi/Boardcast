using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Automation;
using Boardcast.Core;

namespace Boardcast.WindowsTests;

internal static partial class Program
{
    private static async Task RunPickerAsync(string binariesDirectory, bool verifySizes = false)
    {
        string uiPath = Path.Combine(binariesDirectory, "Boardcast.exe");
        if (!File.Exists(uiPath) || !File.Exists(Path.Combine(binariesDirectory, "Boardcast.Daemon.exe")))
            throw new FileNotFoundException("The picker check requires the complete published Windows folder.");
        fixtureRoot = Path.Combine(Path.GetTempPath(), "Boardcast.NativePickerTests-" + Guid.NewGuid().ToString("N"));
        dataDirectory = Path.Combine(fixtureRoot, "data");
        string source = Path.Combine(fixtureRoot, "source");
        const string fileName = "说明.txt", folderName = "资料夹", unselectedName = "未选择.txt";
        Directory.CreateDirectory(Path.Combine(source, folderName));
        await File.WriteAllTextAsync(Path.Combine(source, fileName), "A standalone file selected with the native file dialog.");
        await File.WriteAllTextAsync(Path.Combine(source, folderName, "nested.txt"), "The native folder selection should remain one folder in the send queue.");
        await File.WriteAllTextAsync(Path.Combine(source, unselectedName), "This entry must not be added.");
        new SettingsStore(dataDirectory).Save(new AppSettings
        {
            DeviceName = "Native picker test", Port = FreePort(),
            ReceiveDirectory = Path.Combine(fixtureRoot, "received")
        });
        Process ui = StartUi(uiPath, dataDirectory);
        nint window = await WaitForWindowAsync(ui.Id);
        await WaitUntilAsync(() => Native.IsWindowVisible(window), "picker test main window is visible");
        _ = await WaitForSnapshotAsync();
        Process daemon = Process.GetProcessById(await GetDaemonPidAsync());
        OwnedProcesses.Add(daemon);
        var main = AutomationElement.FromHandle(window);
        var add = FindById(main, "AddItemsButton");
        var selectedItems = FindById(main, "SelectedItems");
        Check(add.Current.ControlType == ControlType.Button && add.Current.IsEnabled, "the Add entry is an accessible enabled button");

        await AddUsingNativePickerAsync(add, ui.Id, folders: false, Path.Combine(source, fileName));
        await WaitUntilAsync(() => QueueElements(selectedItems).Length == 1 && QueueItem(selectedItems, fileName) is not null,
            "the native file selection adds the standalone file");
        await AddUsingNativePickerAsync(add, ui.Id, folders: true, Path.Combine(source, folderName));
        await WaitUntilAsync(() => QueueElements(selectedItems).Length == 2 && QueueItem(selectedItems, folderName) is not null,
            "the native folder selection joins the same send queue");
        Check(!QueueElements(selectedItems).Any(item => item.Current.Name.Contains(unselectedName, StringComparison.Ordinal) ||
            item.Current.Name.Contains("nested.txt", StringComparison.Ordinal)),
            "unselected files and folder contents are not flattened into the queue");

        foreach (bool folders in new[] { false, true })
        {
            nint pickerWindow = await OpenNativePickerAsync(add, ui.Id, folders);
            Native.PostMessage(pickerWindow, Native.WmClose, 0, 0);
            await WaitUntilAsync(() => !Native.IsWindowVisible(pickerWindow), "cancelling the native picker closes its dialog");
            Check(QueueElements(selectedItems).Length == 2, "cancelling the native picker preserves the existing send queue");
            Check(Native.IsWindowVisible(window), "cancelling leaves the main window open");
        }

        if (verifySizes) await VerifyPickerSizesAsync(main, selectedItems, add, ui.Id, source, fileName, folderName);

        Native.PostMessage(window, Native.WmClose, 0, 0);
        await WaitUntilAsync(() => !Native.IsWindowVisible(window), "picker test instance can return to its tray");
        await ExitFromTrayAsync(ui.Id, window);
        await ui.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Check(ui.ExitCode == 0 && daemon.HasExited, "picker test exits both of its isolated processes cleanly");
        Console.WriteLine("Fixture directory: " + fixtureRoot);
        await File.WriteAllTextAsync(Path.Combine(fixtureRoot, "result.json"), JsonSerializer.Serialize(new
        {
            success = true, scenario = verifySizes ? "picker-size" : "picker", checks, completedChecks = CompletedChecks,
            uiPid = ui.Id, daemonPid = daemon.Id, binariesDirectory, measuredAt = DateTimeOffset.Now
        }, BoardcastJson.Options));
    }

    private static async Task<nint> OpenNativePickerAsync(AutomationElement add, int processId, bool folders)
    {
        InvokeControl(add, processId);
        string id = folders ? "AddFoldersMenuItem" : "AddFilesMenuItem";
        AutomationElement? menu = null;
        await WaitUntilAsync(() =>
        {
            menu = FindProcessControl(processId, id);
            return menu is not null && !menu.Current.IsOffscreen;
        }, "the Add menu exposes " + (folders ? "folders" : "files"));
        InvokeControl(menu!, processId);
        nint window = await WaitForOwnedWindowAsync(processId, folders ? "添加文件夹" : "添加文件");
        var picker = AutomationElement.FromHandle(window);
        var className = new StringBuilder(64);
        Native.GetClassName(window, className, className.Capacity);
        Check(className.ToString() == "#32770", "the picker uses the native Windows dialog class");

        return window;
    }

    private static async Task AddUsingNativePickerAsync(AutomationElement add, int processId, bool folders, string path)
    {
        nint window = await OpenNativePickerAsync(add, processId, folders);
        var picker = AutomationElement.FromHandle(window);
        AutomationElement? pathField = null;
        await WaitUntilAsync(() =>
        {
            pathField = picker.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit))
                .Cast<AutomationElement>().FirstOrDefault(element =>
                    !element.Current.IsOffscreen && element.Current.IsEnabled &&
                    element.Current.AutomationId is "1148" or "1152" or "1001" &&
                    element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) && !((ValuePattern)pattern).Current.IsReadOnly);
            return pathField is not null;
        }, "the native picker exposes its editable filename or folder field");
        var pathValue = (ValuePattern)pathField!.GetCurrentPattern(ValuePattern.Pattern);
        TracePickerState("before-entry", picker, pathField);
        pathValue.SetValue(path);
        await WaitUntilAsync(() => pathValue.Current.Value == path, "the native picker field retains the requested path");
        TracePickerState("after-entry", picker, pathField);
        // Native shell descendants can share AutomationId 1; route the dialog's standard IDOK command.
        if (!Native.PostMessage(window, 0x0111, 1, 0))
            throw new InvalidOperationException("Could not post the native dialog IDOK command.");
        try { await WaitUntilAsync(() => !Native.IsWindowVisible(window), "accepting the native selection closes its dialog"); }
        catch { TracePickerState("accept-timeout", picker, pathField); throw; }
    }

    private static void TracePickerState(string stage, AutomationElement picker, AutomationElement? selectedField)
    {
        Console.WriteLine($"TRACE picker {stage}: dialog='{picker.Current.Name}'");
        var controls = picker.FindAll(TreeScope.Descendants, new OrCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)));
        foreach (AutomationElement element in controls)
        {
            try
            {
                if (element.Current.IsOffscreen) continue;
                var parent = TreeWalker.ControlViewWalker.GetParent(element);
                string value = element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) ? ((ValuePattern)pattern).Current.Value : "";
                Console.WriteLine($"TRACE picker control selected={element.Equals(selectedField)}, type={element.Current.ControlType.ProgrammaticName}, id='{element.Current.AutomationId}', name='{element.Current.Name}', enabled={element.Current.IsEnabled}, value='{value}', parentId='{parent?.Current.AutomationId}', parentName='{parent?.Current.Name}'");
            }
            catch (ElementNotAvailableException) { Console.WriteLine("TRACE picker control became unavailable"); }
        }
    }

    private static void InvokeControl(AutomationElement control, int processId)
    {
        if (control.Current.ProcessId != processId || !control.Current.IsEnabled)
            throw new InvalidOperationException("The test control is not owned by the isolated process or is disabled.");
        ((InvokePattern)control.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
    }
    private static AutomationElement? FindProcessControl(int processId, string id)
    {
        AutomationElement? result = null;
        Native.EnumWindows((candidate, _) =>
        {
            Native.GetWindowThreadProcessId(candidate, out uint owner);
            if (owner != processId || !Native.IsWindowVisible(candidate)) return true;
            result = AutomationElement.FromHandle(candidate).FindFirst(TreeScope.Subtree,
                new PropertyCondition(AutomationElement.AutomationIdProperty, id));
            return result is null;
        }, 0);
        return result;
    }

    private static AutomationElement FindById(AutomationElement parent, string id) =>
        parent.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, id))
        ?? throw new InvalidOperationException("Missing accessible control: " + id);

    private static async Task<nint> WaitForOwnedWindowAsync(int processId, string title)
    {
        nint result = 0;
        await WaitUntilAsync(() =>
        {
            Native.EnumWindows((candidate, _) =>
            {
                Native.GetWindowThreadProcessId(candidate, out uint owner);
                if (owner == processId && Native.Title(candidate) == title && Native.IsWindowVisible(candidate))
                { result = candidate; return false; }
                return true;
            }, 0);
            return result != 0;
        }, "the owned window opens: " + title);
        return result;
    }
}
