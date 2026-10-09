using System.Collections;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Boardcast.Core;
using Boardcast.Windows;

namespace Boardcast.LayoutTests;

// No main-window Show/Run: Loaded cannot start the daemon, tray or discovery.
// Preview mode also keeps sidebar preference changes isolated from the user's settings.
internal static class Program
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int checks, failures;
    private static readonly List<string> Results = [];
    private static string? PreviewDirectory;

    [STAThread]
    private static int Main(string[] args)
    {
        if (!args.Contains("--render-preview"))
        {
            Console.Error.WriteLine("Usage: dotnet run --project tests/Boardcast.LayoutTests -- --render-preview");
            return 2;
        }

        var app = new App();
        app.InitializeComponent();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var outputIndex = Array.IndexOf(args, "--output-dir");
        if (outputIndex >= 0 && outputIndex + 1 < args.Length)
            PreviewDirectory = Path.GetFullPath(args[outputIndex + 1]);

        Run("empty layout", EmptyLayout);
        Run("small selection layout", FewLayout);
        Run("file names and metadata adapt across wide and narrow rows", AdaptiveFileRows);
        Run("asynchronous statistics and exclusions remeasure file information", AdaptiveFileMetadataRefresh);
        Run("expanded files reuse adaptive rows without changing selections", AdaptiveExpandedFileRows);
        Run("compact controls keep contextual actions and one-click filter recovery", CompactWorkspace);
        Run("workspace stays stable as content changes", StableWorkspace);
        Run("workspace follows window and column resizing", ResizableWorkspace);
        Run("device actions share one compact row", DeviceActionRow);
        Run("workspace uses the released footer space without status reflow", FooterSpace);
        Run("large-list virtualization and tail access", ManyLayout);
        Run("large device lists scroll inside a stable panel", ManyDevices);
        Run("minimum window with expanded note", MinimumLayout);
        Run("file filtering keeps complete send sources", FileFiltering);
        Run("device filtering preserves multiple recipients", PeerFiltering);
        Run("device groups support same-group and multiple-group filtering", GroupFiltering);
        Run("group filtering removes hidden recipients across background refresh", GroupSelectionSafety);
        Run("personal groups and long names fit the minimum device workspace", GroupLayout);
        Run("nearby device rows keep platform, name and version on one line", SingleRowPeerLayout);
        Run("device groups remain in details without changing filtering", PeerGroupMetadata);
        Run("running-version changes refresh peer details and preserve recipients", PeerVersionDetailsRefresh);
        Run("peer availability details refresh without changing recipients", PeerAvailability);
        Run("sidebar collapse preserves selection", SidebarSelection);
        Run("peer endpoint refresh reaches the action row", UpdateEndpointRefresh);
        Run("same-peer newer version refreshes update settings", UpdateVersionRefresh);
        Run("expanded complete list filters and reaches its tail", ExpandedList);
        Run("workspace omits page heading and keeps connection in sidebar", DirectWorkspace);
        Run("a clickable version opens updates directly from the device row", InlineUpdateIndicator);
        Run("update actions remain compact", CompactUpdateActions);
        foreach (string section in new[] { "general", "rules", "known", "extensions", "updates" })
            Run($"{section} settings adapt to window and navigation sizes", window => ResponsiveSettings(window, section));
        foreach (string section in new[] { "general", "rules", "known", "extensions" })
            Run($"{section} settings retain long content and reach the final row", window => LongSettings(window, section));
        Run("empty settings retain navigation and their creation actions", EmptySettings);
        Run("background update refresh preserves unsaved general settings", SettingsDraftRefresh);
        Run("shared settings editor keeps folder and confirmation actions reachable", SettingsEditorLayout);
        Run("update progress, resume and installation keep compact accessible actions", UpdateSettingsStates);
        Run("cancelled and failed receives retain completed-file actions", ReceiveTransferStates);

        foreach (string result in Results) Console.WriteLine(result);
        Console.WriteLine($"{(failures == 0 ? "PASS" : "FAIL")} {checks} assertions, {failures} scenarios failed");
        app.Shutdown();
        return failures == 0 ? 0 : 1;
    }

    private static void Run(string name, Action<MainWindow> test)
    {
        var window = new MainWindow();
        try
        {
            Set(window, "_previewMode", true);
            test(window);
            Require(!window.IsVisible, "test never displays the main window");
            Require(!Read<bool>(window, "_loaded"), "Loaded never starts the background service");
            Require(Read<Task>(window, "_initializationTask").IsCompletedSuccessfully, "daemon startup remains unstarted");
            Require(Read<object?>(window, "_tray") == null, "no tray icon is created");
            Results.Add("PASS " + name);
        }
        catch (Exception ex)
        {
            failures++;
            while (ex is TargetInvocationException { InnerException: { } inner }) ex = inner;
            Results.Add("FAIL " + name + ": " + ex.Message);
        }
        finally
        {
            Call(window, "ClearSelectedPaths");
            Read<CancellationTokenSource>(window, "_lifetime").Cancel();
            Set(window, "_allowClose", true);
            window.Close();
        }
    }

    private static void Scenario(MainWindow window, string name, bool note = false)
    {
        string[] options = note
            ? ["--preview-scenario", name, "--preview-note"]
            : ["--preview-scenario", name];
        Call(window, "ConfigurePreview", (object)options);
        Layout(window);
    }

    private static FrameworkElement Layout(MainWindow window, bool minimum = false)
    {
        // Measure real controls in a conservative client-area budget: native window
        // borders/caption consume space outside this content tree. No HWND is created.
        double outerWidth = minimum ? window.MinWidth : window.Width;
        double outerHeight = minimum ? window.MinHeight : window.Height;
        double width = Math.Max(1, outerWidth - 16);
        double height = Math.Max(1, outerHeight - 40);
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(width, height));
        content.Arrange(new Rect(0, 0, width, height));
        content.UpdateLayout();
        return content;
    }

    private static void EmptyLayout(MainWindow window)
    {
        Scenario(window, "empty");
        Require(Control<ListBox>(window, "FileList").Items.Count == 0, "empty files list");
        Require(Control<ListBox>(window, "PeerList").Items.Count == 0, "empty peer list");
        Require(Control<FrameworkElement>(window, "EmptyFiles").Visibility == Visibility.Visible, "file empty state appears");
        Require(Control<FrameworkElement>(window, "EmptyPeers").Visibility == Visibility.Visible, "peer empty state appears");
        Require(!Control<Button>(window, "SendButton").IsEnabled, "empty selection cannot send");
        Require(Control<Button>(window, "EmptyFilesActionButton").IsEnabled, "empty selection still permits adding");
        Require(Control<Button>(window, "AddItemsButton").Visibility == Visibility.Collapsed, "empty selection uses a single integrated add entry");
        Within(Control<Button>(window, "EmptyFilesActionButton"), Layout(window), "integrated add action");
        Within(Control<Button>(window, "SendButton"), Layout(window), "send action");
    }

    private static void CompactWorkspace(MainWindow window)
    {
        Scenario(window, "empty");
        Require(Control<FrameworkElement>(window, "FileToolsRow").Visibility == Visibility.Collapsed,
            "empty files do not reserve a tool row");
        var add = Control<Button>(window, "EmptyFilesActionButton");
        Require(add.IsHitTestVisible && add.Focusable, "integrated add permits mouse and keyboard activation");
        Scenario(window, "few");
        Require(Control<Button>(window, "AddItemsButton").Visibility == Visibility.Visible,
            "a populated list keeps a discoverable add-more entry");
        Require(Control<FrameworkElement>(window, "FileToolsRow").Visibility == Visibility.Collapsed,
            "a small unselected list has no extra tool row");
        var list = Control<ListBox>(window, "FileList");
        list.SelectedItem = list.Items.Cast<object>().First(item => (bool)item.GetType().GetProperty("IsDirectory")!.GetValue(item)!);
        Layout(window);
        Require(Control<Button>(window, "ExcludeItemsButton").Visibility == Visibility.Visible &&
            Control<Button>(window, "RemoveItemsButton").Visibility == Visibility.Visible &&
            Control<FrameworkElement>(window, "FileToolsRow").Visibility == Visibility.Visible,
            "selecting a folder reveals exclusion and removal together");
        var count = list.Items.Count;
        Control<TextBox>(window, "FileSearch").Text = "no-matching-file";
        Layout(window);
        Require(list.Items.Count == 0 && add.IsEnabled, "no-match state offers an enabled recovery");
        Set(window, "_busy", true);
        Call(window, "UpdateSend");
        Require(!add.IsEnabled, "integrated empty-state action respects busy state");
        Set(window, "_busy", false);
        Call(window, "UpdateSend");
        add.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Layout(window);
        Require(Control<TextBox>(window, "FileSearch").Text.Length == 0 && list.Items.Count == count,
            "one click restores all files without changing send sources");
        Call(window, "SetSidebarCollapsed", false);
        var workspace = Control<Grid>(window, "DevicesPage");
        workspace.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
        workspace.ColumnDefinitions[2].Width = new GridLength(280);
        var snapshot = Read<DaemonSnapshot>(window, "_snapshot");
        snapshot.Settings.PeerGroupFilter = DeviceGroupFilter.SelectedGroups;
        snapshot.Settings.VisibleGroups = Enumerable.Range(0, 123).Select(i => "Group-" + i).ToList();
        Call(window, "RenderPeerList");
        var root = Layout(window, minimum: true);
        var header = Control<Grid>(window, "PeerHeaderRow");
        FrameworkElement[] controls = [
            Control<TextBlock>(window, "NearbyDevicesTitle"), Control<Button>(window, "GroupFilterButton"),
            Control<TextBlock>(window, "PeerCount"), Control<Button>(window, "RefreshPeersButton")];
        foreach (var control in controls) Within(control, header, "compact device header control");
        double center = controls[0].TranslatePoint(new Point(0, controls[0].ActualHeight / 2), root).Y;
        Require(controls.All(control => Math.Abs(control.TranslatePoint(new Point(0, control.ActualHeight / 2), root).Y - center) < 1),
            "title, group selection, statistics and refresh stay on the same row at 280 pixels");
        for (var i = 1; i < controls.Length; i++)
            Require(controls[i].TranslatePoint(new Point(), root).X >=
                controls[i - 1].TranslatePoint(new Point(controls[i - 1].ActualWidth, 0), root).X - 1,
                "compact device header controls do not overlap");
        SavePreview(window, "home-compact-header-minimum.png");
    }

    private static void FewLayout(MainWindow window)
    {
        Scenario(window, "few");
        Require(Control<ListBox>(window, "FileList").Items.Count == 2, "both selected roots appear");
        Require(Control<FrameworkElement>(window, "EmptyFiles").Visibility == Visibility.Collapsed, "empty state is hidden");
        Require(Control<Button>(window, "SendButton").IsEnabled, "valid selection can send");
        Require(Recipients(window).Count == 1, "one recipient is retained");
        Require(Control<FrameworkElement>(window, "FileSearchRow").Visibility == Visibility.Collapsed, "few roots do not need search row");
        Within(Control<ListBox>(window, "FileList"), Layout(window), "small files list");
        var fewList = Control<ListBox>(window, "FileList");
        var lastRow = (FrameworkElement)fewList.ItemContainerGenerator.ContainerFromIndex(1)!;
        Within(lastRow, fewList, "both small-selection rows");
        Require(Visuals<ScrollViewer>(fewList).First().ScrollableHeight <= 0.001, "two selected roots do not need a scrollbar");
        Within(Control<Button>(window, "SendButton"), Layout(window), "send action");
    }

    private static (AdaptiveFileInfoPanel Panel, TextBlock Name, Grid Metadata) FileInfo(DependencyObject root)
    {
        var panel = Visuals<AdaptiveFileInfoPanel>(root).Single();
        return (panel, panel.Children.OfType<TextBlock>().Single(), panel.Children.OfType<Grid>().Single());
    }

    private static ContentPresenter FilePresentation(MainWindow window, object item)
    {
        var presenter = new ContentPresenter
        {
            Content = item,
            ContentTemplate = (DataTemplate)window.FindResource("FileItemTemplate")
        };
        presenter.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        presenter.Arrange(new Rect(0, 0, presenter.DesiredSize.Width, presenter.DesiredSize.Height));
        return presenter;
    }

    private static void LayoutFilePresentation(ContentPresenter presenter, double width)
    {
        presenter.Measure(new Size(width, double.PositiveInfinity));
        presenter.Arrange(new Rect(0, 0, width, presenter.DesiredSize.Height));
        presenter.UpdateLayout();
    }

    private static bool FileInformationSharesLine(DependencyObject root)
    {
        var (panel, name, metadata) = FileInfo(root);
        return SameBand(RowBounds(name, panel), RowBounds(metadata, panel));
    }

    private static object AddLongFile(MainWindow window)
    {
        var paths = Read<IList>(window, "_paths");
        var item = Activator.CreateInstance(paths[0]!.GetType(),
            [@"C:\Design\完整文件名需要保留在悬停详情中-" + new string('W', 96) + ".pdf",
                false, "2.4 MiB", Read<CancellationTokenSource>(window, "_lifetime").Token])!;
        paths.Add(item);
        Call(window, "RenderPaths");
        Layout(window);
        return item;
    }

    private static void AdaptiveFileRows(MainWindow window)
    {
        Scenario(window, "few");
        var list = Control<ListBox>(window, "FileList");
        object item = list.Items[0];
        list.SelectedItem = item;
        object[] originalPaths = Read<IList>(window, "_paths").Cast<object>().ToArray();
        string[] recipients = Recipients(window).Select(peer => peer.Fingerprint).ToArray();
        var presenter = FilePresentation(window, item);
        var (_, name, metadata) = FileInfo(presenter);
        double naturalWidth = presenter.DesiredSize.Width;
        double wideWidth = naturalWidth + 40;
        double narrowWidth = naturalWidth - Math.Min(name.DesiredSize.Width, metadata.DesiredSize.Width) / 2;
        double naturalNameWidth = name.DesiredSize.Width;
        double naturalMetadataWidth = metadata.DesiredSize.Width;
        LayoutFilePresentation(presenter, wideWidth);
        var panel = FileInfo(presenter).Panel;
        Require(FileInformationSharesLine(presenter),
            "a file name and complete statistics share one line when their natural widths fit");
        Require(name.ActualWidth >= naturalNameWidth - 1 && metadata.ActualWidth >= naturalMetadataWidth - 1,
            "single-line file information reserves the complete natural name and statistics widths");
        Require(RowBounds(name, panel).Right < RowBounds(metadata, panel).Left &&
            Math.Abs(RowBounds(metadata, panel).Right - panel.ActualWidth) <= 1,
            "single-line statistics sit to the right without overlapping the file name");
        double inlineHeight = panel.ActualHeight;
        LayoutFilePresentation(presenter, narrowWidth);
        Require(!FileInformationSharesLine(presenter) &&
            RowBounds(metadata, panel).Top > RowBounds(name, panel).Bottom,
            "insufficient combined width restores the name and statistics to separate lines");
        Require(panel.ActualHeight > inlineHeight &&
            name.TextTrimming == TextTrimming.CharacterEllipsis && name.TextWrapping == TextWrapping.NoWrap,
            "fallback preserves the existing compact two-line name trimming behavior");
        LayoutFilePresentation(presenter, wideWidth);
        Require(FileInformationSharesLine(presenter) && Math.Abs(panel.ActualHeight - inlineHeight) <= 1,
            "widening the same bound row returns it to one line without stale measurements");
        Require(Read<IList>(window, "_paths").Cast<object>().SequenceEqual(originalPaths) &&
            ReferenceEquals(list.SelectedItem, item) &&
            Recipients(window).Select(peer => peer.Fingerprint).SequenceEqual(recipients),
            "width changes preserve the complete source queue, file selection and recipients");

        object longFile = AddLongFile(window);
        var longPresenter = FilePresentation(window, longFile);
        var longParts = FileInfo(longPresenter);
        double completeNameWidth = longParts.Name.DesiredSize.Width;
        LayoutFilePresentation(longPresenter, wideWidth);
        Require(!FileInformationSharesLine(longPresenter) && longParts.Name.ActualWidth < completeNameWidth,
            "a file name that cannot fit even by itself falls back to two lines at the same allocation");
        Require(longParts.Name.Text == (string)longFile.GetType().GetProperty("Name")!.GetValue(longFile)! &&
            longParts.Name.TextTrimming == TextTrimming.CharacterEllipsis &&
            Equals(longParts.Name.ToolTip, longFile.GetType().GetProperty("Path")!.GetValue(longFile)),
            "a trimmed long file name retains its full text and complete path tooltip");
    }

    private static void AdaptiveFileMetadataRefresh(MainWindow window)
    {
        Scenario(window, "few");
        var list = Control<ListBox>(window, "FileList");
        object item = list.Items.Cast<object>().Single(path =>
            (bool)path.GetType().GetProperty("IsDirectory")!.GetValue(path)!);
        list.SelectedItem = item;
        object[] originalPaths = Read<IList>(window, "_paths").Cast<object>().ToArray();
        var sizeProperty = item.GetType().GetProperty("SizeText")!;
        sizeProperty.SetValue(item, "正在统计…");
        var presenter = FilePresentation(window, item);
        double fixedWidth = presenter.DesiredSize.Width + 20;
        LayoutFilePresentation(presenter, fixedWidth);
        Require(FileInformationSharesLine(presenter), "a short pending statistic fits beside the folder name");
        const string completedStatistics = "124.6 MiB · 48 个文件";
        // Folder scanning completes asynchronously, then the UI continuation publishes the result.
        var completed = Task.Run(() => completedStatistics).GetAwaiter().GetResult();
        sizeProperty.SetValue(item, completed);
        LayoutFilePresentation(presenter, fixedWidth);
        var parts = FileInfo(presenter);
        string observedStatistics = Texts(parts.Metadata);
        Require(observedStatistics.Contains(completedStatistics),
            "background statistics binding has refreshed before measurement assertions; actual=" +
            observedStatistics.Replace('\n', '|'));
        var sizeText = Visuals<TextBlock>(parts.Metadata).Single(text => text.Text == completedStatistics);
        Require(!FileInformationSharesLine(presenter) && Equals(sizeText.ToolTip, completedStatistics),
            "a completed background statistic updates its binding and triggers two-line fallback at the same width");
        sizeProperty.SetValue(item, Task.Run(() => "正在统计…").GetAwaiter().GetResult());
        LayoutFilePresentation(presenter, fixedWidth);
        Require(FileInformationSharesLine(presenter),
            "a shorter asynchronous statistic returns the existing row to one line");

        string[] excluded = ["cache", "临时资料"];
        item.GetType().GetMethod("SetExclusions")!.Invoke(item, [excluded]);
        sizeProperty.SetValue(item, completedStatistics);
        LayoutFilePresentation(presenter, fixedWidth);
        parts = FileInfo(presenter);
        string observedExclusions = Texts(parts.Metadata);
        Require(observedExclusions.Contains("已排除 2 项") && observedExclusions.Contains(completedStatistics),
            "statistics and exclusion bindings have refreshed before marker assertions; actual=" +
            observedExclusions.Replace('\n', '|'));
        var exclusion = Visuals<TextBlock>(parts.Metadata).Single(text => text.Text == "已排除 2 项");
        Require(exclusion.Visibility == Visibility.Visible && exclusion.ActualWidth > 0 &&
            Texts(parts.Metadata).Contains(completedStatistics),
            "exclusion status stays visible alongside refreshed statistics in fallback metadata");
        Require(RowBounds(exclusion, parts.Metadata).Right <= parts.Metadata.ActualWidth + 1 &&
            RowBounds(exclusion, parts.Metadata).Left >= 0,
            "the complete exclusion marker stays within the available metadata line");
        var source = (TransferSource)item.GetType().GetMethod("ToSource")!.Invoke(item, null)!;
        string automation = (string)item.GetType().GetProperty("AutomationLabel")!.GetValue(item)!;
        Require(source.ExcludedPaths.SequenceEqual(excluded) &&
            automation.Contains(completedStatistics) && automation.Contains("已排除 2 项"),
            "adaptive presentation retains both actual source exclusions and complete accessibility statistics");
        Require(Read<IList>(window, "_paths").Cast<object>().SequenceEqual(originalPaths) &&
            ReferenceEquals(list.SelectedItem, item),
            "asynchronous statistics and exclusion presentation preserve queue membership and the selected folder");
    }

    private static void AdaptiveExpandedFileRows(MainWindow window)
    {
        Scenario(window, "few");
        object longFile = AddLongFile(window);
        var mainList = Control<ListBox>(window, "FileList");
        object selected = mainList.Items[0];
        mainList.SelectedItem = selected;
        object[] originalPaths = Read<IList>(window, "_paths").Cast<object>().ToArray();
        var expanded = (Window)Call(window, "CreateExpandedFilesWindow")!;
        try
        {
            var root = LayoutContent(expanded);
            var list = Visuals<ListBox>(root).Single();
            Require(ReferenceEquals(list.ItemTemplate, mainList.ItemTemplate),
                "the expanded file view reuses the same adaptive file template as the main workspace");
            var shortRow = (FrameworkElement)list.ItemContainerGenerator.ContainerFromItem(selected)!;
            var longRow = (FrameworkElement)list.ItemContainerGenerator.ContainerFromItem(longFile)!;
            Require(FileInformationSharesLine(shortRow) && !FileInformationSharesLine(longRow),
                "the expanded view independently fits short names and retains two lines for long names");
            Within(FileInfo(shortRow).Panel, shortRow, "expanded adaptive short file information");
            Within(FileInfo(longRow).Panel, longRow, "expanded adaptive long file information");
            list.SelectedItem = longFile;
            Visuals<TextBox>(root).Single().Text = (string)longFile.GetType().GetProperty("Name")!.GetValue(longFile)!;
            LayoutContent(expanded);
            Require(list.Items.Count == 1 && ReferenceEquals(list.Items[0], longFile),
                "expanded filtering still finds the full long file name after adaptive layout");
            Require(Read<IList>(window, "_paths").Cast<object>().SequenceEqual(originalPaths) &&
                ReferenceEquals(mainList.SelectedItem, selected),
                "expanded selection and filtering leave the source queue and main file selection unchanged");
        }
        finally { expanded.Close(); }
    }

    private static void StableWorkspace(MainWindow window)
    {
        Scenario(window, "empty");
        var root = Layout(window);
        var files = Control<FrameworkElement>(window, "SendFilesPanel");
        var devices = Control<FrameworkElement>(window, "DeviceArea");
        var workspace = Control<Grid>(window, "DevicesPage");
        var send = Control<Button>(window, "SendButton");
        double baseline = workspace.ActualHeight;
        double sendTop = send.TranslatePoint(new Point(), root).Y;
        Require(Math.Abs(files.ActualHeight - baseline) < 1 && Math.Abs(devices.ActualHeight - baseline) < 1,
            "empty panels reserve the available workspace rather than shrink to their placeholders");
        SavePreview(window, "home-empty.png");
        Scenario(window, "few");
        Require(Math.Abs(files.ActualHeight - baseline) < 1 && Math.Abs(devices.ActualHeight - baseline) < 1,
            "a few files and devices do not move the panel edges");
        Require(Math.Abs(send.TranslatePoint(new Point(), root).Y - sendTop) < 1,
            "adding content keeps the send action anchored");
        SavePreview(window, "home-few.png");
        var list = Control<ListBox>(window, "FileList");
        double listHeight = list.ActualHeight;
        Control<Expander>(window, "NoteExpander").IsExpanded = true;
        Layout(window);
        Require(list.ActualHeight < listHeight - 50, "expanding the note reallocates space inside the file panel");
        Require(Math.Abs(files.ActualHeight - baseline) < 1, "expanding the note preserves the outer frame");
        Control<Expander>(window, "NoteExpander").IsExpanded = false;
        Layout(window);
        Call(window, "ClearSelectedPaths");
        Scenario(window, "many");
        Require(Math.Abs(files.ActualHeight - baseline) < 1 && Math.Abs(devices.ActualHeight - baseline) < 1,
            "a full queue uses the same workspace with scrolling");
        Control<TextBox>(window, "FileSearch").Text = "this-name-does-not-exist";
        Control<TextBox>(window, "PeerSearch").Text = "this-name-does-not-exist";
        Layout(window);
        Require(Math.Abs(files.ActualHeight - baseline) < 1 && Math.Abs(devices.ActualHeight - baseline) < 1,
            "filtering to no matches does not collapse either panel");
        Require(Math.Abs(send.TranslatePoint(new Point(), root).Y - sendTop) < 1,
            "filtering keeps the send action anchored");
    }

    private static void ResizableWorkspace(MainWindow window)
    {
        Scenario(window, "few");
        var files = Control<FrameworkElement>(window, "SendFilesPanel");
        var devices = Control<FrameworkElement>(window, "DeviceArea");
        var workspace = Control<Grid>(window, "DevicesPage");
        double initialHeight = files.ActualHeight;
        window.Width = 1240;
        window.Height = 780;
        Layout(window);
        Require(files.ActualHeight > initialHeight + 150, "larger windows provide more usable list space");
        Require(Math.Abs(files.ActualHeight - workspace.ActualHeight) < 1 && Math.Abs(devices.ActualHeight - workspace.ActualHeight) < 1,
            "both panels adapt to the larger window");
        double initialWidth = files.ActualWidth;
        workspace.ColumnDefinitions[0].Width = new GridLength(400);
        Layout(window);
        Require(files.ActualWidth < initialWidth - 80, "the file-device divider can still change the column allocation");
        Require(devices.ActualWidth >= 280, "resizing preserves a usable device column");
        Require(Visuals<GridSplitter>(workspace).Single().ResizeBehavior == GridResizeBehavior.PreviousAndNext,
            "the interactive divider retains both adjacent columns");
        SavePreview(window, "home-large-window.png");
    }

    private static void DeviceActionRow(MainWindow window)
    {
        Scenario(window, "few");
        var root = Layout(window);
        var row = Control<WrapPanel>(window, "DeviceActions");
        var connect = Control<Button>(window, "ConnectPeerButton");
        var details = Control<Button>(window, "ManagePeerButton");
        var folder = Control<Button>(window, "ReceiveFolderButton");
        var actions = new[] { connect, details, folder };
        Require(actions.All(button => ReferenceEquals(button.Parent, row)),
            "IP connection, device details, and receive folder share the device action row");
        Require(actions.All(button => button.FontSize == connect.FontSize && button.Padding == connect.Padding && button.ActualHeight == connect.ActualHeight),
            "the three device actions use consistent text, padding, and height");
        double baseline = connect.TranslatePoint(new Point(), root).Y;
        Require(actions.All(button => Math.Abs(button.TranslatePoint(new Point(), root).Y - baseline) < 1),
            "all three device actions fit on one row at the default window size");
        Require(folder.TranslatePoint(new Point(), root).X > details.TranslatePoint(new Point(), root).X,
            "the receive folder action follows device details");
        Require(folder.ActualHeight <= 36, "receive folder uses the same compact quiet button scale");
        Call(window, "SetSidebarCollapsed", false);
        root = Layout(window, minimum: true);
        foreach (var action in actions) Within(action, root, $"device action '{action.Content}' at minimum size");
        Within(row, Control<FrameworkElement>(window, "DeviceArea"), "device actions stay inside the device area");
        Require(Visuals<Button>(root).Count(button => Equals(button.Content, "打开接收文件夹")) == 1,
            "the receive folder entry is present only once");
        SavePreview(window, "home-device-actions-minimum.png");
    }

    private static void FooterSpace(MainWindow window)
    {
        Scenario(window, "few");
        var root = Layout(window);
        var files = Control<FrameworkElement>(window, "SendFilesPanel");
        var devices = Control<FrameworkElement>(window, "DeviceArea");
        var send = Control<Button>(window, "SendButton");
        var notice = Control<FrameworkElement>(window, "StatusNotice");
        var workspace = Control<Grid>(window, "MainWorkspace");
        double filesBottom = files.TranslatePoint(new Point(0, files.ActualHeight), root).Y;
        double devicesBottom = devices.TranslatePoint(new Point(0, devices.ActualHeight), root).Y;
        double sendTop = send.TranslatePoint(new Point(), root).Y;
        Require(notice.Visibility == Visibility.Collapsed, "empty status feedback is hidden");
        Require(Math.Abs(root.ActualHeight - filesBottom - workspace.Margin.Bottom) < 1,
            "file panel reaches the normal bottom margin with no reserved footer");
        Require(Math.Abs(root.ActualHeight - devicesBottom - workspace.Margin.Bottom) < 1,
            "device panel reaches the normal bottom margin with no reserved footer");
        double filesHeight = files.ActualHeight, devicesHeight = devices.ActualHeight;
        Call(window, "SetStatus", "已连接，正在自动查找附近设备。");
        Layout(window);
        Require(notice.Visibility == Visibility.Visible, "an action can display transient feedback");
        Require(Math.Abs(files.ActualHeight - filesHeight) < 1 && Math.Abs(devices.ActualHeight - devicesHeight) < 1 &&
            Math.Abs(send.TranslatePoint(new Point(), root).Y - sendTop) < 1,
            "showing status feedback does not shrink either panel or move the send action");
        Require(notice.TranslatePoint(new Point(0, notice.ActualHeight), root).Y < sendTop,
            "status feedback stays above the primary send action");
        Require(notice.TranslatePoint(new Point(0, notice.ActualHeight), root).Y <=
            Control<FrameworkElement>(window, "DeviceActions").TranslatePoint(new Point(), root).Y,
            "status feedback stays above the device utility actions");
        Within(notice, root, "status feedback");
        SavePreview(window, "home-status-notice.png");
        Call(window, "SetStatus", "无法写入接收目录：目标文件夹不可用或没有写入权限。请检查当前设备的接收位置，并确认移动硬盘或网络共享仍然在线；修正接收位置后，可以从传输记录继续重试，已经通过校验的数据会继续保留，无需重新发送已完成的部分。");
        Layout(window);
        Require(Control<TextBlock>(window, "StatusText").ActualHeight > 30 && notice.ActualWidth <= 681,
            "long errors wrap across readable lines within the bounded feedback surface");
        Require(Math.Abs(files.ActualHeight - filesHeight) < 1 && Math.Abs(devices.ActualHeight - devicesHeight) < 1,
            "long feedback also leaves the expanded workspace intact");
        Within(notice, root, "wrapped status feedback");
        SavePreview(window, "home-status-wrapped.png");
        Call(window, "HideStatusNotice");
        Layout(window);
        Require(notice.Visibility == Visibility.Collapsed && Math.Abs(files.ActualHeight - filesHeight) < 1 &&
            Math.Abs(devices.ActualHeight - devicesHeight) < 1,
            "dismissing feedback preserves the full workspace geometry");
        Call(window, "SetStatus", "");
        Layout(window);
        Require(notice.Visibility == Visibility.Collapsed, "an empty status cannot reserve a message area");
        SavePreview(window, "home-full-height.png");
    }

    private static void ManyLayout(MainWindow window)
    {
        Scenario(window, "many");
        var list = Control<ListBox>(window, "FileList");
        Require(list.Items.Count == 1002, "all 1002 selected roots remain available");
        Require(Control<FrameworkElement>(window, "FileSearchRow").Visibility == Visibility.Visible, "large list offers visible search");
        var scroll = Visuals<ScrollViewer>(list).First();
        Require(scroll.ExtentHeight > scroll.ViewportHeight, "large list actually scrolls");
        int realized = Visuals<ListBoxItem>(list).Count();
        Require(realized > 0 && realized < 100, "list virtualizes item containers rather than realizing all 1002");
        SavePreview(window, "home-many.png");
        list.ScrollIntoView(list.Items[list.Items.Count - 1]);
        Layout(window);
        Require(list.ItemContainerGenerator.ContainerFromIndex(1001) != null, "last selected file can be reached by scrolling");
        Require(Control<Button>(window, "ExpandFilesButton").IsEnabled, "full-list entry is available for large lists");
        Require(Recipients(window).Count == 2, "two recipients survive large-list rendering");
        Results.Add($"METRIC large files: viewport={list.ActualHeight:0.0}, realized={realized}, total={list.Items.Count}");
    }

    private static void ManyDevices(MainWindow window)
    {
        Scenario(window, "few");
        var root = Layout(window);
        var list = Control<ListBox>(window, "PeerList");
        var area = Control<FrameworkElement>(window, "DeviceArea");
        var send = Control<Button>(window, "SendButton");
        double areaHeight = area.ActualHeight;
        double sendTop = send.TranslatePoint(new Point(), root).Y;
        var snapshot = Read<DaemonSnapshot>(window, "_snapshot");
        snapshot.Peers.AddRange(Enumerable.Range(1, 250).Select(index => new PeerInfo
        {
            Fingerprint = index.ToString("x64"), Name = $"协作设备-{index:000}", Address = $"192.168.2.{index}",
            Platform = "windows", AppVersion = "0.2.0", Runtime = AppRelease.RuntimeIdentifier
        }));
        Call(window, "RenderPeerList");
        Layout(window);
        Require(list.Items.Count == 253, "all nearby devices remain accessible");
        Require(Control<FrameworkElement>(window, "PeerSearchRow").Visibility == Visibility.Visible,
            "many devices reveal a filter inside the panel");
        Require(Math.Abs(area.ActualHeight - areaHeight) < 1 && Math.Abs(send.TranslatePoint(new Point(), root).Y - sendTop) < 1,
            "device discovery does not move the panel edge or send action");
        var scroll = Visuals<ScrollViewer>(list).First();
        Require(scroll.ExtentHeight > scroll.ViewportHeight, "many devices scroll within their allocated list space");
        int realized = Visuals<ListBoxItem>(list).Count();
        Require(realized > 0 && realized < 40, "device rows remain virtualized");
        SavePreview(window, "home-many-devices.png");
        list.ScrollIntoView(list.Items[list.Items.Count - 1]);
        Layout(window);
        Require(list.ItemContainerGenerator.ContainerFromIndex(list.Items.Count - 1) != null,
            "the last discovered device can be reached by scrolling");
    }

    private static void MinimumLayout(MainWindow window)
    {
        Scenario(window, "many", note: true);
        var root = Layout(window, minimum: true);
        var list = Control<ListBox>(window, "FileList");
        Require(list.ActualHeight >= 119, "expanded note leaves at least a 120 DIP file viewport");
        Within(list, root, "file viewport");
        Within(Control<TextBox>(window, "SummaryBox"), root, "expanded note");
        Within(Control<Button>(window, "ClearItemsButton"), root, "clear action");
        Within(Control<Button>(window, "SendButton"), root, "send action");
        Within(Control<Button>(window, "RefreshPeersButton"), root, "refresh action");
        list.ScrollIntoView(list.Items[list.Items.Count - 1]);
        Layout(window, minimum: true);
        Require(list.ItemContainerGenerator.ContainerFromIndex(1001) != null, "tail item remains reachable at minimum size");
        SavePreview(window, "home-minimum-note.png");
        Results.Add($"METRIC minimum outer={window.MinWidth:0}x{window.MinHeight:0}, files viewport={list.ActualHeight:0.0}");
    }

    private static void FileFiltering(MainWindow window)
    {
        Scenario(window, "many");
        var list = Control<ListBox>(window, "FileList");
        var search = Control<TextBox>(window, "FileSearch");
        int originalSources = Read<IList>(window, "_paths").Count;
        search.Text = "设计素材-1000";
        Layout(window);
        Require(list.Items.Count == 1, "search reduces displayed files to one match");
        Require(Read<IList>(window, "_paths").Count == originalSources, "search does not remove send sources");
        Require(Control<Button>(window, "SendButton").IsEnabled, "filter does not disable an otherwise valid send");
        search.Text = "this-name-does-not-exist";
        Layout(window);
        Require(list.Items.Count == 0, "unmatched query displays no rows");
        Require(Control<FrameworkElement>(window, "EmptyFiles").Visibility == Visibility.Visible, "unmatched query has an empty state");
        Require(Read<IList>(window, "_paths").Count == originalSources, "unmatched query still keeps all send sources");
        search.Clear();
        Layout(window);
        Require(list.Items.Count == 1002, "clearing search restores every source");
    }

    private static void PeerFiltering(MainWindow window)
    {
        Scenario(window, "many");
        var search = Control<TextBox>(window, "PeerSearch");
        var peerList = Control<ListBox>(window, "PeerList");
        string[] selected = Recipients(window).Select(p => p.Fingerprint).Order().ToArray();
        search.Text = "客厅";
        Layout(window);
        Require(peerList.Items.Count == 1, "device search shows the matching device");
        Require(Recipients(window).Select(p => p.Fingerprint).Order().SequenceEqual(selected), "filter preserves both previously chosen recipients");
        Require(Control<Button>(window, "SendButton").Content.ToString()!.Contains("2"), "send button states full recipient count");
        search.Clear();
        Layout(window);
        Require(peerList.SelectedItems.Count == 2, "clearing search restores the selected rows");
        Require(Recipients(window).Select(p => p.Fingerprint).Order().SequenceEqual(selected), "restored rows retain original identities");
        Call(window, "ClearPeersClick", Control<Button>(window, "ClearPeersButton"), new RoutedEventArgs(ButtonBase.ClickEvent));
        Require(Recipients(window).Count == 0 && peerList.SelectedItems.Count == 0, "clear-selection action removes visible and hidden recipients");
        Require(!Control<Button>(window, "SendButton").IsEnabled, "clearing recipients disables send");
    }

    private static DaemonSnapshot GroupingScenario(MainWindow window)
    {
        Scenario(window, "few");
        var snapshot = Read<DaemonSnapshot>(window, "_snapshot");
        snapshot.Settings.DeviceName = "Design-本机";
        snapshot.Settings.GroupName = "";
        snapshot.Settings.PeerGroupFilter = DeviceGroupFilter.All;
        snapshot.Settings.VisibleGroups = [];
        snapshot.Settings.Devices = [];
        snapshot.Peers =
        [
            new PeerInfo { Fingerprint = 101.ToString("x64"), Name = "Design-笔记本", Address = "192.168.1.101", Platform = "windows" },
            new PeerInfo { Fingerprint = 102.ToString("x64"), Name = "会议设备", GroupName = "design", Address = "192.168.1.102", Platform = "windows" },
            new PeerInfo { Fingerprint = 103.ToString("x64"), Name = "Ops-服务器", Address = "192.168.1.103", Platform = "windows" },
            new PeerInfo { Fingerprint = 104.ToString("x64"), Name = "Studio-归档", GroupName = "共享团队", Address = "192.168.1.104", Platform = "windows" },
            new PeerInfo { Fingerprint = 105.ToString("x64"), Name = "Solo", Address = "192.168.1.105", Platform = "windows" },
            new PeerInfo { Fingerprint = 106.ToString("x64"), Name = " -- ", Address = "192.168.1.106", Platform = "windows" }
        ];
        snapshot.Settings.Devices.Add(new KnownDevice
        {
            Fingerprint = snapshot.Peers[2].Fingerprint, Name = snapshot.Peers[2].Name, Alias = "Design-服务器备注"
        });
        snapshot.Settings.Devices.Add(new KnownDevice
        {
            Fingerprint = snapshot.Peers[3].Fingerprint, Name = snapshot.Peers[3].Name, GroupName = "私人项目"
        });
        RefreshGroups(window);
        return snapshot;
    }

    private static void RefreshGroups(MainWindow window)
    {
        Call(window, "RenderPeerList");
        Call(window, "UpdateSend");
        Layout(window);
    }

    private static string[] VisiblePeerIds(MainWindow window) => Control<ListBox>(window, "PeerList").Items
        .Cast<object>().Select(row => Peer(row).Fingerprint).Order().ToArray();

    private static void GroupFiltering(MainWindow window)
    {
        var snapshot = GroupingScenario(window);
        var peers = snapshot.Peers.ToArray();
        Require(VisiblePeerIds(window).Length == peers.Length, "all groups initially expose every discovered device");
        snapshot.Settings.PeerGroupFilter = DeviceGroupFilter.SameGroup;
        RefreshGroups(window);
        Require(VisiblePeerIds(window).SequenceEqual(new[] { peers[0].Fingerprint, peers[1].Fingerprint }.Order()),
            "same-group mode combines the local name prefix with case-insensitive explicit peer groups");
        Require(!VisiblePeerIds(window).Contains(peers[2].Fingerprint),
            "a local display alias does not move a device into the alias prefix group");
        Require(Control<TextBlock>(window, "PeerCount").Text == "2 / 6 台",
            "the nearby device count distinguishes visible devices from all discovered devices");
        snapshot.Settings.GroupName = "私人项目";
        RefreshGroups(window);
        Require(VisiblePeerIds(window).SequenceEqual(new[] { peers[3].Fingerprint }),
            "a custom local group matches the user's personal override for a nearby device");
        snapshot.Settings.PeerGroupFilter = DeviceGroupFilter.SelectedGroups;
        snapshot.Settings.VisibleGroups = [" ops ", "私人项目"];
        RefreshGroups(window);
        Require(VisiblePeerIds(window).SequenceEqual(new[] { peers[2].Fingerprint, peers[3].Fingerprint }.Order()),
            "multiple selected groups form a case-insensitive union with trimmed names");
        snapshot.Peers.Add(peers[2] with { Fingerprint = 107.ToString("x64"), Name = "Ops-新增设备", Address = "192.168.1.107" });
        RefreshGroups(window);
        Require(VisiblePeerIds(window).Length == 3 && VisiblePeerIds(window).Contains(snapshot.Peers[^1].Fingerprint),
            "background discovery applies the saved group filter to newly arriving devices");
        snapshot.Settings.VisibleGroups = [];
        RefreshGroups(window);
        Require(VisiblePeerIds(window).Length == 0, "an explicitly empty group selection shows no devices");
        Require(Control<FrameworkElement>(window, "EmptyPeers").Visibility == Visibility.Visible,
            "an empty group selection explains the empty device list");
        var showAll = Control<Button>(window, "ShowAllPeersButton");
        Require(showAll.Visibility == Visibility.Visible && showAll.IsEnabled,
            "the empty group state offers a reachable show-all recovery action");
        Within(showAll, Control<FrameworkElement>(window, "DeviceArea"), "show-all group recovery");
        SavePreview(window, "home-groups-empty.png");
        Control<TextBox>(window, "PeerSearch").Text = "also-no-text-match";
        showAll.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Layout(window);
        Require(snapshot.Settings.PeerGroupFilter == DeviceGroupFilter.All,
            "show-all recovery stores the all-groups mode");
        Require(Control<TextBox>(window, "PeerSearch").Text.Length == 0 && VisiblePeerIds(window).Length == snapshot.Peers.Count,
            "show-all recovery clears text filtering and restores every discovered device");
    }

    private static void GroupSelectionSafety(MainWindow window)
    {
        var snapshot = GroupingScenario(window);
        var list = Control<ListBox>(window, "PeerList");
        var selectedIds = new[] { snapshot.Peers[0].Fingerprint, snapshot.Peers[2].Fingerprint, snapshot.Peers[3].Fingerprint };
        foreach (object row in list.Items.Cast<object>().Where(row => selectedIds.Contains(Peer(row).Fingerprint)).ToArray())
            list.SelectedItems.Add(row);
        Require(Recipients(window).Count == 3, "the group scenario begins with recipients from three groups");
        snapshot.Settings.PeerGroupFilter = DeviceGroupFilter.SelectedGroups;
        snapshot.Settings.VisibleGroups = ["Design"];
        RefreshGroups(window);
        string retained = snapshot.Peers[0].Fingerprint;
        Require(Recipients(window).Select(peer => peer.Fingerprint).SequenceEqual(new[] { retained }),
            "group filtering removes recipients that are no longer in an allowed group");
        Require(list.SelectedItems.Count == 1 && Control<Button>(window, "SendButton").IsEnabled,
            "the send action retains only the still-visible recipient");
        Control<TextBox>(window, "PeerSearch").Text = "no-visible-text-match";
        Layout(window);
        Require(list.Items.Count == 0 && Recipients(window).Single().Fingerprint == retained,
            "text search preserves a chosen recipient within the allowed group");
        Control<TextBox>(window, "PeerSearch").Clear();
        snapshot.Settings.PeerGroupFilter = DeviceGroupFilter.All;
        RefreshGroups(window);
        Require(Recipients(window).Single().Fingerprint == retained && list.SelectedItems.Count == 1,
            "revealing all groups does not reselect recipients removed by the group filter");
        snapshot.Peers[2] = snapshot.Peers[2] with { Address = "192.168.1.203", AppVersion = "0.9.0" };
        RefreshGroups(window);
        Require(Recipients(window).Single().Fingerprint == retained && list.SelectedItems.Count == 1,
            "a background device refresh cannot reselect removed recipients");
        snapshot.Settings.PeerGroupFilter = DeviceGroupFilter.SameGroup;
        RefreshGroups(window);
        snapshot.Peers[0] = snapshot.Peers[0] with { GroupName = "Ops" };
        Require(Recipients(window).Count == 0,
            "sending revalidates changed group membership before the next list render");
        RefreshGroups(window);
        Require(list.SelectedItems.Count == 0 && !Control<Button>(window, "SendButton").IsEnabled,
            "a selected peer moving to a hidden group clears selection and disables send");
        snapshot.Settings.PeerGroupFilter = DeviceGroupFilter.All;
        RefreshGroups(window);
        Require(Recipients(window).Count == 0 && list.SelectedItems.Count == 0,
            "restoring all groups after a peer changes groups still requires deliberate reselection");
    }

    private static void GroupLayout(MainWindow window)
    {
        var snapshot = GroupingScenario(window);
        string groupName = "个人项目 · 跨设备协作归档与设计文件长期保存" + new string('W', 36);
        snapshot.Settings.GroupName = groupName;
        snapshot.Settings.Devices.Single(device => device.Fingerprint == snapshot.Peers[3].Fingerprint).GroupName = groupName;
        snapshot.Settings.PeerGroupFilter = DeviceGroupFilter.SameGroup;
        RefreshGroups(window);
        Call(window, "SetSidebarCollapsed", false);
        var root = Layout(window, minimum: true);
        var list = Control<ListBox>(window, "PeerList");
        Require(list.Items.Count == 1, "a long custom group still matches the personal device group");
        object row = list.Items[0];
        string displayedGroup = (string)row.GetType().GetProperty("GroupName")!.GetValue(row)!;
        Require(displayedGroup == groupName, "the peer row exposes the complete personal group instead of the advertised group");
        var container = (FrameworkElement)list.ItemContainerGenerator.ContainerFromIndex(0)!;
        string details = PeerValue<string>(row, "Details");
        Require(details.Contains("分组：" + groupName) && Equals(container.ToolTip, details),
            "a long personal group remains complete in the compact device row's hover details");
        Require(System.Windows.Automation.AutomationProperties.GetName(container).Contains(groupName),
            "accessibility details retain the full personal group without a separate visual badge");
        Require(!Visuals<TextBlock>(container).Any(text => text.ActualHeight > 0 && text.Text == groupName),
            "a long personal group does not add a separate visible line to the compact device row");
        Within(container, list, "long personal group device row");
        Within(Control<Button>(window, "GroupFilterButton"), Control<FrameworkElement>(window, "DeviceArea"), "minimum-window group selector");
        Within(Control<TextBlock>(window, "PeerCount"), Control<FrameworkElement>(window, "DeviceArea"), "minimum-window visible device count");
        Within(Control<Button>(window, "SendButton"), root, "minimum-window send action with groups");
        Require(Visuals<ScrollViewer>(list).First().ScrollableWidth <= 1,
            "a long group name does not require horizontal device-list scrolling");
        SavePreview(window, "home-groups-long-minimum.png");
        OpenSettings(window, "general");
        root = Layout(window, minimum: true);
        AssertSettingsLayout(window, "general", root);
        var groupInput = Visuals<TextBox>(SettingsPanel(window, "general"))
            .Single(input => System.Windows.Automation.AutomationProperties.GetName(input) == "我的分组");
        Require(groupInput.Text == groupName, "settings retain the complete editable local group name");
        groupInput.BringIntoView();
        Layout(window, minimum: true);
        Within(groupInput, SettingsScroll(window, "general"), "minimum-window custom group editor");
        SavePreview(window, "settings-groups-long-minimum.png");
    }
    private static (object Row, FrameworkElement Container) PeerRow(MainWindow window, string fingerprint)
    {
        var list = Control<ListBox>(window, "PeerList");
        object row = list.Items.Cast<object>().Single(item => Peer(item).Fingerprint == fingerprint);
        return (row, (FrameworkElement)(list.ItemContainerGenerator.ContainerFromItem(row) ??
            throw new InvalidOperationException("The peer row has no realized container.")));
    }

    private static T PeerValue<T>(object row, string name) =>
        (T)row.GetType().GetProperty(name)!.GetValue(row)!;

    private static Rect RowBounds(FrameworkElement element, FrameworkElement row) =>
        element.TransformToAncestor(row).TransformBounds(new Rect(element.RenderSize));

    private static bool SameBand(Rect first, Rect second) =>
        Math.Min(first.Bottom, second.Bottom) - Math.Max(first.Top, second.Top) > 0;

    private static void SingleRowPeerLayout(MainWindow window)
    {
        Scenario(window, "few");
        var snapshot = Read<DaemonSnapshot>(window, "_snapshot");
        var peer = snapshot.Peers[0];
        var known = snapshot.Settings.Devices.Single(device => device.Fingerprint == peer.Fingerprint);
        known.Alias = "设计工作站 · 用于跨设备协作与完整发行包归档" + new string('W', 32);
        known.GroupName = "共享项目 · 长期协作归档与设计资料" + new string('W', 32);
        Call(window, "SetSidebarCollapsed", false);
        var workspace = Control<Grid>(window, "DevicesPage");
        workspace.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
        workspace.ColumnDefinitions[2].Width = new GridLength(280);
        RefreshGroups(window);
        foreach (bool minimum in new[] { false, true })
        {
            Layout(window, minimum);
            var (item, row) = PeerRow(window, peer.Fingerprint);
            var name = Visuals<TextBlock>(row).Single(text => text.Text == known.Alias);
            var icon = Visuals<System.Windows.Shapes.Path>(row).Single(path => Equals(path.ToolTip, PeerValue<string>(item, "PlatformLabel")));
            var version = Visuals<Button>(row).Single(button => button.Visibility == Visibility.Visible && button.ActualHeight > 0);
            var nameBounds = RowBounds(name, row);
            Require(SameBand(nameBounds, RowBounds(icon, row)) && SameBand(nameBounds, RowBounds(version, row)),
                "platform icon, long device name and actionable version share one horizontal line");
            Require(RowBounds(icon, row).Right <= nameBounds.Left + 1 &&
                RowBounds(version, row).Left >= nameBounds.Right - 1,
                "the compact platform icon and version marker do not overlap the device name");
            Require(row.ActualHeight <= 48 && name.ActualHeight <= 24 &&
                Visuals<TextBlock>(row).Where(text => text.ActualHeight > 0)
                    .All(text => SameBand(RowBounds(text, row), nameBounds)),
                "all visible device text fits a single compact information line");
            Require(icon.Data is not null && Equals(icon.ToolTip, "Windows"),
                "the compact Windows icon retains its platform identity in hover information");
            Require(version.Content?.ToString() == "v" + peer.UpdateVersion &&
                PeerValue<bool>(item, "CanOpenUpdate"),
                "a compatible newer version is the row's single actionable version marker");
            Require(!Visuals<TextBlock>(row).Any(text => text.ActualHeight > 0 &&
                (text.Text == "有更新" || text.Text == known.GroupName || text.Text == "已信任")),
                "the single row reserves no separate update, group or trust label");
            Within(name, row, "single-line device name");
            Within(icon, row, "single-line platform icon");
            Within(version, row, "single-line version action");
            Within(row, Control<ListBox>(window, "PeerList"), "single-line peer item");
            Require(Visuals<ScrollViewer>(Control<ListBox>(window, "PeerList")).First().ScrollableWidth <= 1,
                "single-line device items fit a 280-pixel column without horizontal scrolling");
            string details = PeerValue<string>(item, "Details");
            Require(details.Contains(known.Alias) && details.Contains(known.GroupName) &&
                details.Contains(peer.Address) && details.Contains("版本：v" + peer.AppVersion) &&
                Equals(name.ToolTip, details),
                "compact name hover retains complete name, group, address and running version");
        }
        var ordinaryPeer = snapshot.Peers[2];
        var (ordinaryItem, ordinaryRow) = PeerRow(window, ordinaryPeer.Fingerprint);
        var ordinaryVersion = Visuals<TextBlock>(ordinaryRow)
            .Single(text => text.ActualHeight > 0 && text.Text == "v" + ordinaryPeer.AppVersion);
        Require(!PeerValue<bool>(ordinaryItem, "CanOpenUpdate") &&
            !Visuals<Button>(ordinaryRow).Any(button => button.Visibility == Visibility.Visible && button.ActualHeight > 0),
            "an ordinary or older running version stays a static marker rather than an update action");
        Require(ordinaryVersion.Foreground is SolidColorBrush ordinaryForeground &&
            ordinaryForeground.Color == ((SolidColorBrush)window.FindResource("Muted")).Color,
            "an ordinary or older version uses the muted gray marker styling");
        Within(ordinaryVersion, ordinaryRow, "static ordinary version marker");
        SavePreview(window, "home-device-single-line-minimum.png");

        snapshot.Peers[2] = ordinaryPeer with { Runtime = AppRelease.RuntimeIdentifier, UpdateVersion = "0.3.1", UpdatePort = 45902 };
        RefreshGroups(window);
        Require(new[] { peer.Fingerprint, ordinaryPeer.Fingerprint }.All(fingerprint =>
            PeerValue<bool>(PeerRow(window, fingerprint).Row, "CanOpenUpdate")),
            "each compatible newer source exposes its own actionable version instead of only the recommended source");
        var secondUpdate = PeerRow(window, ordinaryPeer.Fingerprint);
        Require(Visuals<Button>(secondUpdate.Container).Single(button => button.Visibility == Visibility.Visible &&
            button.ActualHeight > 0).Content?.ToString() == "v0.3.1",
            "a second compatible source labels its own available version");
    }

    private static void PeerGroupMetadata(MainWindow window)
    {
        var snapshot = GroupingScenario(window);
        var peer = snapshot.Peers[4];
        snapshot.Settings.PeerGroupFilter = DeviceGroupFilter.SelectedGroups;
        snapshot.Settings.VisibleGroups = ["solo"];
        RefreshGroups(window);
        var list = Control<ListBox>(window, "PeerList");
        var (item, row) = PeerRow(window, peer.Fingerprint);
        list.SelectedItem = item;
        Require(list.Items.Count == 1 && PeerValue<string>(item, "GroupName") == "Solo",
            "a repeated automatic group still matches the selected group filter");
        Require(PeerValue<string>(item, "Details").Contains("分组：Solo") &&
            Equals(row.ToolTip, PeerValue<string>(item, "Details")),
            "a group omitted from the compact row remains available in complete hover details");
        var known = new KnownDevice { Fingerprint = peer.Fingerprint, Name = peer.Name, Alias = "独立归档设备" };
        snapshot.Settings.Devices.Add(known);
        RefreshGroups(window);
        (item, row) = PeerRow(window, peer.Fingerprint);
        Require(VisiblePeerIds(window).SequenceEqual(new[] { peer.Fingerprint }) &&
            PeerValue<string>(item, "GroupName") == "Solo",
            "a display alias changes neither automatic grouping nor filtered membership");
        Require(PeerValue<string>(item, "Details").Contains("分组：Solo") &&
            System.Windows.Automation.AutomationProperties.GetName(row).Contains("Solo"),
            "group metadata remains discoverable in hover and accessibility details");
        Require(!Visuals<TextBlock>(row).Any(text => text.ActualHeight > 0 && text.Text == "Solo"),
            "group metadata does not add a standalone label to the aliased device row");
        Require(Recipients(window).Single().Fingerprint == peer.Fingerprint && list.SelectedItems.Count == 1,
            "moving group information to details preserves the selected recipient");
        known.Alias = "solo";
        RefreshGroups(window);
        (item, row) = PeerRow(window, peer.Fingerprint);
        Require(PeerValue<string>(item, "GroupName") == "Solo" &&
            PeerValue<string>(item, "Details").Contains("分组：Solo"),
            "a case-only alias preserves the complete actual group in details");
        snapshot.Settings.GroupName = "Solo";
        snapshot.Settings.PeerGroupFilter = DeviceGroupFilter.SameGroup;
        RefreshGroups(window);
        Require(VisiblePeerIds(window).SequenceEqual(new[] { peer.Fingerprint }) &&
            Recipients(window).Single().Fingerprint == peer.Fingerprint,
            "group metadata continues to participate in same-group filtering");
    }

    private static void PeerVersionDetailsRefresh(MainWindow window)
    {
        Scenario(window, "few");
        var snapshot = Read<DaemonSnapshot>(window, "_snapshot");
        var peer = snapshot.Peers[0] with { UpdateVersion = "", UpdatePort = 0 };
        snapshot.Peers[0] = peer;
        RefreshGroups(window);
        var (original, _) = PeerRow(window, peer.Fingerprint);
        Require(PeerValue<string>(original, "Details").Contains("版本：v" + peer.AppVersion) &&
            !PeerValue<bool>(original, "CanOpenUpdate"),
            "the initial ordinary peer exposes its running version in hover details");
        const string changedVersion = "0.3.1";
        snapshot.Peers[0] = peer with { AppVersion = changedVersion };
        RefreshGroups(window);
        var (item, row) = PeerRow(window, peer.Fingerprint);
        string details = PeerValue<string>(item, "Details");
        Require(Peer(item).AppVersion == changedVersion && Peer(item).UpdateVersion == peer.UpdateVersion &&
            Peer(item).Port == peer.Port && Peer(item).UpdatePort == peer.UpdatePort,
            "an isolated running-version change refreshes the row without changing update metadata or endpoints");
        Require(details.Contains("版本：v" + changedVersion) && !details.Contains("版本：v" + peer.AppVersion),
            "hover details replace the obsolete running version even when update metadata is unchanged");
        Require(Equals(row.ToolTip, details) && details.Contains(peer.Address),
            "the arranged row binds the refreshed version and complete address to its tooltip");
        Require(System.Windows.Automation.AutomationProperties.GetName(row).Contains("v" + changedVersion),
            "compact peer accessibility retains the refreshed running version");
        Require(PeerValue<string>(item, "VersionLabel") == "v" + changedVersion &&
            Visuals<TextBlock>(row).Any(text => text.ActualHeight > 0 && text.Text == "v" + changedVersion),
            "the static compact version marker refreshes when only the running version changes");
        Require(Recipients(window).Single().Fingerprint == peer.Fingerprint &&
            Control<ListBox>(window, "PeerList").SelectedItems.Count == 1,
            "refreshing only a running version preserves the selected sending identity");
    }

    private static void PeerAvailability(MainWindow window)
    {
        Scenario(window, "few");
        var snapshot = Read<DaemonSnapshot>(window, "_snapshot");
        var peer = snapshot.Peers[0];
        snapshot.Peers = [peer];
        snapshot.Settings.Devices.Single(device => device.Fingerprint == peer.Fingerprint).GroupName = "协作";
        foreach (var state in new[]
        {
            (Peer: peer with { ReceivingEnabled = false }, Warning: "已暂停接收"),
            (Peer: peer with { DepartureHint = true }, Warning: "可能已离开"),
            (Peer: peer with { LanOnline = false }, Warning: "已离线")
        })
        {
            snapshot.Peers[0] = state.Peer;
            RefreshGroups(window);
            var (item, row) = PeerRow(window, peer.Fingerprint);
            string details = PeerValue<string>(item, "Details");
            Require(PeerValue<string>(item, "Availability").Contains(state.Warning) &&
                details.Contains(state.Warning) &&
                System.Windows.Automation.AutomationProperties.GetName(row).Contains(state.Warning),
                "availability changes refresh compact state, hover and accessibility information");
            Require(details.Contains("Windows") && details.Contains("已信任") && details.Contains("分组：协作") &&
                details.Contains(peer.Address) && details.Contains("版本：v" + peer.AppVersion),
                "compact details retain platform, trust, group, address and running version alongside warnings");
            Require(Equals(row.ToolTip, details),
                "the arranged device row exposes the latest complete state details");
            var stateIcon = Visuals<TextBlock>(row).Single(text => text.ActualHeight > 0 && text.Text == "⚠");
            var name = Visuals<TextBlock>(row).Single(text => text.Text == PeerValue<string>(item, "DisplayName"));
            Require(Equals(stateIcon.ToolTip, state.Warning) && SameBand(RowBounds(stateIcon, row), RowBounds(name, row)),
                "an availability icon conveys the warning without adding another device line");
            Require(Recipients(window).Single().Fingerprint == peer.Fingerprint,
                "an availability refresh retains the deliberately selected device identity");
            if (!state.Peer.ReceivingEnabled || !state.Peer.LanOnline)
                Require(!Control<Button>(window, "SendButton").IsEnabled,
                    "compact status presentation keeps unavailable recipients from enabling send");
            if (state.Peer.DepartureHint || !state.Peer.LanOnline)
                Require(!PeerValue<bool>(item, "CanOpenUpdate"),
                    "departing and offline peers stop presenting a clickable update version");
        }
        snapshot.Peers[0] = peer;
        RefreshGroups(window);
        var (restored, restoredRow) = PeerRow(window, peer.Fingerprint);
        Require(PeerValue<string>(restored, "Availability").Length == 0 &&
            !PeerValue<string>(restored, "Details").Contains("已暂停接收") &&
            !PeerValue<string>(restored, "Details").Contains("可能已离开") &&
            !PeerValue<string>(restored, "Details").Contains("已离线") &&
            !Visuals<TextBlock>(restoredRow).Any(text => text.ActualHeight > 0 && text.Text == "⚠"),
            "returning peer metadata removes obsolete warning details and the warning icon");
        Require(Control<Button>(window, "SendButton").IsEnabled && PeerValue<bool>(restored, "CanOpenUpdate"),
            "restored availability returns the valid send and update-version actions");
    }

    private static void SidebarSelection(MainWindow window)
    {
        Scenario(window, "many");
        var files = Control<ListBox>(window, "FileList");
        files.SelectedItems.Add(files.Items[2]);
        files.SelectedItems.Add(files.Items[3]);
        object[] selectedFiles = files.SelectedItems.Cast<object>().ToArray();
        string[] selectedPeers = Recipients(window).Select(p => p.Fingerprint).Order().ToArray();
        var column = Control<ColumnDefinition>(window, "SidebarColumn");
        var button = Control<Button>(window, "ToggleSidebarButton");
        double initial = column.ActualWidth;
        Call(window, "ToggleSidebarClick", button, new RoutedEventArgs(ButtonBase.ClickEvent));
        Layout(window);
        Require(Math.Abs(column.ActualWidth - initial) > 50, "sidebar toggle changes real allocated width");
        Require(files.SelectedItems.Cast<object>().SequenceEqual(selectedFiles), "sidebar toggle preserves file selection");
        Require(Recipients(window).Select(p => p.Fingerprint).Order().SequenceEqual(selectedPeers), "sidebar toggle preserves recipients");
        Call(window, "ToggleSidebarClick", button, new RoutedEventArgs(ButtonBase.ClickEvent));
        Layout(window);
        Require(Math.Abs(column.ActualWidth - initial) < 1, "second toggle restores initial width");
        Require(files.SelectedItems.Cast<object>().SequenceEqual(selectedFiles), "restoring sidebar preserves file selection");
    }

    private static void UpdateEndpointRefresh(MainWindow window)
    {
        Scenario(window, "few");
        var snapshot = Read<DaemonSnapshot>(window, "_snapshot");
        var peer = snapshot.Peers[0];
        int newPort = peer.UpdatePort + 7;
        snapshot.Peers[0] = peer with { UpdatePort = newPort, Port = peer.Port + 3 };
        Call(window, "RenderPeerList");
        Layout(window);
        object row = Control<ListBox>(window, "PeerList").Items.Cast<object>()
            .Single(p => Peer(p).Fingerprint == peer.Fingerprint);
        Require(Peer(row).UpdatePort == newPort, "update action row uses refreshed endpoint after same-version peer restart");
        Require(Peer(row).Port == peer.Port + 3, "device details row uses refreshed transfer endpoint");
        Require(Recipients(window).Single().Fingerprint == peer.Fingerprint, "metadata refresh preserves selected identity");
    }

    private static void UpdateVersionRefresh(MainWindow window)
    {
        Scenario(window, "few");
        Set(window, "_settingsSection", "updates");
        Call(window, "ShowPage", "settings");
        Call(window, "RefreshUpdateUi");
        Layout(window);
        var panel = Control<Panel>(window, "SettingsContent");
        Require(Texts(panel).Contains("v0.3.0"), "initial recommended version is displayed");
        var snapshot = Read<DaemonSnapshot>(window, "_snapshot");
        snapshot.Peers[0] = snapshot.Peers[0] with { UpdateVersion = "0.4.0", AppVersion = "0.4.0", UpdatePort = 59002 };
        Call(window, "RenderPeerList");
        Call(window, "RefreshUpdateUi");
        Layout(window);
        Require(Texts(panel).Contains("v0.4.0"), "same peer's newer release refreshes visible settings");
        Require(!Texts(panel).Contains("v0.3.0"), "obsolete recommended version is removed");
    }


    private static void ExpandedList(MainWindow window)
    {
        Scenario(window, "many");
        var expanded = (Window)Call(window, "CreateExpandedFilesWindow")!;
        try
        {
            var root = LayoutContent(expanded);
            var list = Visuals<ListBox>(root).Single();
            var search = Visuals<TextBox>(root).Single();
            Require(!expanded.IsVisible, "expanded list is measured without displaying a modal window");
            Require(list.Items.Count == 1002, "expanded view contains complete 1002-item queue");
            Require(list.ActualHeight > Control<ListBox>(window, "FileList").ActualHeight + 100,
                "expanded view gives substantially more space to the complete queue");
            Within(list, root, "expanded complete list");
            list.ScrollIntoView(list.Items[1001]);
            LayoutContent(expanded);
            Require(list.ItemContainerGenerator.ContainerFromIndex(1001) != null, "expanded view reaches the final file");
            search.Text = "设计素材-1000";
            LayoutContent(expanded);
            Require(list.Items.Count == 1, "expanded search finds the final file");
            Require(Read<IList>(window, "_paths").Count == 1002, "expanded search preserves complete send queue");
            search.Clear();
            LayoutContent(expanded);
            Require(list.Items.Count == 1002, "expanded search clearing restores full queue");
            Within(Visuals<Button>(root).Single(), root, "expanded view return action");
        }
        finally { expanded.Close(); }
    }

    private static void DirectWorkspace(MainWindow window)
    {
        Scenario(window, "few");
        var root = Layout(window);
        Require(Control<TextBlock>(window, "PageTitle").ActualHeight == 0, "page heading reserves no workspace height");
        Require(Control<TextBlock>(window, "PageSubtitle").ActualHeight == 0, "page subtitle reserves no workspace height");
        var connection = Control<Button>(window, "ConnectionButton");
        Rect bounds = connection.TransformToAncestor(root).TransformBounds(new Rect(connection.RenderSize));
        Require(bounds.Right <= Control<ColumnDefinition>(window, "SidebarColumn").ActualWidth + 1,
            "connection action is entirely in the sidebar");
        Require(bounds.Top > root.ActualHeight / 2, "connection action is in bottom device-information area");
        Within(connection, root, "sidebar connection action");
    }

    private static FrameworkElement LayoutContent(Window window)
    {
        var content = (FrameworkElement)window.Content;
        double width = Math.Max(1, window.Width - 16), height = Math.Max(1, window.Height - 40);
        content.Measure(new Size(width, height));
        content.Arrange(new Rect(0, 0, width, height));
        content.UpdateLayout();
        return content;
    }


    private static void InlineUpdateIndicator(MainWindow window)
    {
        Scenario(window, "few");
        var peers = Control<ListBox>(window, "PeerList");
        var row = (FrameworkElement)peers.ItemContainerGenerator.ContainerFromIndex(0)!;
        object item = peers.Items[0];
        var update = Visuals<Button>(row).Single(button => button.Visibility == Visibility.Visible && button.ActualHeight > 0);
        Require(update.Content?.ToString() == "v0.3.0" && PeerValue<bool>(item, "CanOpenUpdate"),
            "the applicable newer version itself is the clickable update marker");
        Require(!Visuals<TextBlock>(row).Any(text => text.ActualHeight > 0 && text.Text == "有更新"),
            "the row adds no separate update bubble beside the version marker");
        var name = Visuals<TextBlock>(row).Single(text => text.Text == PeerValue<string>(item, "DisplayName"));
        Rect nameBounds = RowBounds(name, row);
        Rect updateBounds = RowBounds(update, row);
        Require(SameBand(nameBounds, updateBounds),
            "device name and actionable version occupy one horizontal band");
        Require(updateBounds.Left >= nameBounds.Right - 1, "the actionable version follows the name without overlap");
        Require(update.Foreground is SolidColorBrush foreground &&
            foreground.Color == ((SolidColorBrush)window.FindResource("Accent")).Color,
            "the actionable version uses the blue accent to distinguish it from a static version");
        // Indicator navigation must not attempt IPC or pop up a download confirmation.
        update.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Require(Read<string>(window, "_page") == "settings" && Read<string>(window, "_settingsSection") == "updates",
            "clicking update indicator opens update settings");
        Require(Recipients(window).Count == 1, "update navigation preserves the sending recipient");
        Require(!Read<bool>(window, "_busy"), "indicator navigation does not start a transfer or download");
    }

    private static void CompactUpdateActions(MainWindow window)
    {
        Scenario(window, "updates");
        var panel = Control<Panel>(window, "SettingsContent");
        var actions = Visuals<Button>(panel).Where(button => button.IsVisible || button.ActualHeight > 0).ToArray();
        Require(actions.Length > 0, "update settings expose an explicit update action");
        foreach (var action in actions)
        {
            Require(action.ActualHeight <= 40, $"update action '{action.Content}' has a compact height");
            Require(action.ActualWidth <= 240 && action.ActualWidth < panel.ActualWidth * .7,
                $"update action '{action.Content}' is sized to its label rather than the entire settings panel");
        }
        SavePreview(window, "settings-updates.png");
    }

    private static void ResponsiveSettings(MainWindow window, string section)
    {
        Scenario(window, "few");
        PopulateSettings(window);
        OpenSettings(window, section);
        foreach (bool collapsed in new[] { true, false })
        {
            Call(window, "SetSidebarCollapsed", collapsed);
            foreach (bool minimum in new[] { false, true })
            {
                var scroll = SettingsScroll(window, section);
                scroll.ScrollToTop();
                var root = Layout(window, minimum);
                AssertSettingsLayout(window, section, root);
                string size = minimum ? "minimum" : "default";
                string navigation = collapsed ? "collapsed" : "expanded";
                SavePreview(window, $"settings-{section}-{size}-{navigation}.png");
            }
        }
        if (section == "rules")
        {
            var menus = SettingButtons(SettingsPanel(window, section)).Where(button => button.ContextMenu != null).Select(button => button.ContextMenu!).ToArray();
            Require(menus.Length == Read<DaemonSnapshot>(window, "_snapshot").Settings.Rules.Count,
                "every rule retains an accessible secondary action menu");
            for (int index = 0; index < menus.Length; index++)
            {
                var items = menus[index].Items.OfType<MenuItem>().ToArray();
                Require(items.Any(item => Equals(item.Header, "删除") && item.IsEnabled), "each rule menu retains delete");
                Require(items.Single(item => Equals(item.Header, "上移")).IsEnabled == (index > 0),
                    "moving a rule upward is available except for the first rule");
            }
        }
        Require(Recipients(window).Count == 1, "settings navigation leaves the selected sending recipient intact");
    }

    private static void LongSettings(MainWindow window, string section)
    {
        Scenario(window, "few");
        PopulateSettings(window, longContent: true);
        OpenSettings(window, section);
        Call(window, "SetSidebarCollapsed", false);
        var root = Layout(window, minimum: true);
        var panel = SettingsPanel(window, section);
        if (section == "general")
        {
            var diagnostics = Visuals<Expander>(panel).Single();
            Require(!diagnostics.IsExpanded, "diagnostics are initially tucked away");
            diagnostics.IsExpanded = true;
            Layout(window, minimum: true);
        }
        AssertSettingsLayout(window, section, root);
        var snapshot = Read<DaemonSnapshot>(window, "_snapshot");
        switch (section)
        {
            case "general":
                var values = Visuals<TextBox>(panel).Select(input => input.Text).ToArray();
                Require(values.Contains(snapshot.Settings.DeviceName) && values.Contains(snapshot.Settings.ReceiveDirectory),
                    "long device name and receive path remain complete editable values");
                var fingerprint = Visuals<TextBlock>(panel).Single(text => text.Text == snapshot.Fingerprint);
                Require(fingerprint.ActualHeight > 0 && FitsHorizontally(fingerprint, panel),
                    "expanding diagnostics reveals the complete fingerprint within the content width");
                var copy = SettingButtons(panel).Single(button => button.Content?.ToString() == "复制");
                copy.BringIntoView();
                Layout(window, minimum: true);
                Within(copy, SettingsScroll(window, section), "expanded diagnostics copy action");
                SettingsScroll(window, section).ScrollToTop();
                Layout(window, minimum: true);
                break;
            case "rules":
                Require(snapshot.Settings.Rules.All(rule => Texts(panel).Contains(rule.Name)), "all long rule names remain in the rendered list");
                Require(snapshot.Settings.Rules.All(rule => Texts(panel).Contains(rule.Destination)), "complete long receive destinations remain available");
                var disabled = Visuals<TextBlock>(panel).Single(text => text.Text.Contains("已停用"));
                Require(disabled.TextWrapping == TextWrapping.Wrap && disabled.TextTrimming == TextTrimming.None,
                    "a long rule name cannot truncate the separate disabled-state label");
                disabled.BringIntoView();
                Layout(window, minimum: true);
                Within(disabled, SettingsScroll(window, section), "long rule disabled-state label");
                SavePreview(window, "settings-rules-long-disabled.png");
                SettingsScroll(window, section).ScrollToTop();
                Layout(window, minimum: true);
                break;
            case "known":
                Require(snapshot.Settings.Devices.All(device => Texts(panel).Contains(device.DisplayName)), "all long saved device names remain in the rendered list");
                break;
            case "extensions":
                Require(snapshot.Settings.Integrations.All(tool => Texts(panel).Contains(tool.Name)), "all long configured tool names remain in the rendered list");
                break;
        }
        SavePreview(window, $"settings-{section}-long-minimum.png");
        var scroll = SettingsScroll(window, section);
        Require(scroll.ScrollableHeight > 0, "long settings content scrolls inside the window");
        scroll.ScrollToEnd();
        Layout(window, minimum: true);
        Require(Math.Abs(scroll.VerticalOffset - scroll.ScrollableHeight) < 1, "the settings list can reach its bottom");
        FrameworkElement last = section == "general"
            ? Visuals<TextBlock>(panel).Last(text => text.Text.Contains("诊断记录 08"))
            : SettingButtons(panel).Last();
        Within(last, scroll, "the final settings row action or diagnostic");
        AssertSettingsLayout(window, section, root);
        SavePreview(window, $"settings-{section}-long-tail.png");
    }

    private static void EmptySettings(MainWindow window)
    {
        Scenario(window, "empty");
        var snapshot = Read<DaemonSnapshot>(window, "_snapshot");
        snapshot.Settings.Devices.Clear();
        snapshot.Settings.Rules.Clear();
        snapshot.Settings.Integrations.Clear();
        Call(window, "SetSidebarCollapsed", false);
        foreach (string section in new[] { "rules", "known", "extensions", "updates" })
        {
            OpenSettings(window, section);
            var root = Layout(window, minimum: true);
            AssertSettingsLayout(window, section, root);
            var panel = SettingsPanel(window, section);
            Require(Texts(panel).Length > 0, "an empty settings page still explains the section");
            if (section is "rules" or "extensions")
            {
                var action = SettingButtons(panel).Single(button => button.Content?.ToString() == (section == "rules" ? "新建规则" : "添加工具"));
                Require(action.IsEnabled, "an empty list retains its create action");
                Within(action, SettingsScroll(window, section), "empty settings creation action");
            }
            SavePreview(window, $"settings-{section}-empty.png");
        }
    }

    private static void SettingsEditorLayout(MainWindow window)
    {
        // The editor requires an owner HWND. A separate hidden owner avoids showing
        // MainWindow and therefore cannot trigger its daemon or discovery lifecycle.
        var owner = new Window { Width = 1, Height = 1, ShowInTaskbar = false };
        new System.Windows.Interop.WindowInteropHelper(owner).EnsureHandle();
        EditorDialog? dialog = null;
        try
        {
            dialog = new EditorDialog(owner, "编辑接收规则");
            dialog.AddText("规则名称", "设计素材归档");
            dialog.AddCheck("启用", true);
            dialog.AddChoice("设备", ["任何设备（自动接收仍要求信任）", "设计工作站"], 1);
            dialog.AddChoice("内容类型", ["文件和文件夹", "仅散文件", "仅文件夹"], 0);
            dialog.AddText("允许的后缀，用逗号分隔；留空表示全部", ".png, .jpg, .pdf, .svg");
            dialog.AddText("顶层文件夹名称模式，如 Design-*；留空不限", "Design-*");
            dialog.AddText("总大小上限（MiB）；0 表示使用全局上限", "512");
            var folder = dialog.AddFolder("接收目录；留空使用默认目录", @"D:\团队共享资料\年度设计归档\最终交付与项目资料");
            dialog.AddCheck("满足规则时自动接收（仍须设备信任和许可）", true);
            var root = LayoutContent(dialog);
            var confirm = Visuals<Button>(root).Single(button => Equals(button.Content, "确定"));
            var cancel = Visuals<Button>(root).Single(button => Equals(button.Content, "取消"));
            Within(confirm, root, "editor confirmation action");
            Within(cancel, root, "editor cancel action");
            Require(confirm.IsDefault && cancel.IsCancel, "editor retains keyboard confirmation and cancellation");
            SavePreview(dialog, "settings-editor-rule-default.png");

            dialog.Width = dialog.MinWidth;
            dialog.Height = dialog.MinHeight;
            root = LayoutContent(dialog);
            var scroll = Visuals<ScrollViewer>(root).First();
            Require(scroll.ScrollableHeight > 0, "a long editor scrolls within its minimum window");
            Within(confirm, root, "editor minimum-window confirmation");
            double confirmTop = confirm.TranslatePoint(new Point(), root).Y;
            scroll.ScrollToEnd();
            LayoutContent(dialog);
            var browse = Visuals<Button>(root).Single(button => Equals(button.Content, "选择文件夹"));
            Within(folder, scroll, "editor receive folder field");
            Within(browse, scroll, "editor inline folder chooser");
            Require(Math.Abs(folder.TranslatePoint(new Point(), root).Y - browse.TranslatePoint(new Point(), root).Y) < 1,
                "folder path and folder chooser stay on the same row");
            Require(browse.ActualWidth <= 140 && browse.ActualHeight <= 32,
                "folder chooser keeps a compact size at minimum width");
            Require(Math.Abs(confirm.TranslatePoint(new Point(), root).Y - confirmTop) < 1,
                "scrolling the editor does not move its confirmation footer");
            Require(scroll.ScrollableWidth <= 1 && !dialog.IsVisible && !owner.IsVisible,
                "editor needs no horizontal scrolling and stays hidden during preview");
            SavePreview(dialog, "settings-editor-rule-minimum-tail.png");
        }
        finally
        {
            dialog?.Close();
            owner.Close();
        }
    }

    private static void SettingsDraftRefresh(MainWindow window)
    {
        Scenario(window, "few");
        PopulateSettings(window);
        OpenSettings(window, "general");
        Layout(window);
        var panel = SettingsPanel(window, "general");
        var inputs = Visuals<TextBox>(panel).ToArray();
        var name = inputs.Single(input => System.Windows.Automation.AutomationProperties.GetName(input) == "设备名称");
        var folder = inputs.Single(input => System.Windows.Automation.AutomationProperties.GetName(input) == "接收文件夹");
        var group = inputs.Single(input => System.Windows.Automation.AutomationProperties.GetName(input) == "我的分组");
        var receiving = Visuals<CheckBox>(panel).Single(input => System.Windows.Automation.AutomationProperties.GetName(input) == "允许接收新请求");
        name.Text = "尚未保存的设备名称";
        folder.Text = @"D:\尚未保存的接收位置";
        receiving.IsChecked = false;
        group.Text = "尚未保存的个人项目组";
        var snapshot = Read<DaemonSnapshot>(window, "_snapshot");
        snapshot.Peers[0] = snapshot.Peers[0] with { AppVersion = "0.5.0", UpdateVersion = "0.5.0", UpdatePort = 59002, GroupName = "后台刷新的对方分组" };
        Call(window, "RenderPeerList");
        Call(window, "RefreshUpdateUi");
        Layout(window);
        var filterChange = (Task<bool>)Call(window, "SetGroupFilterAsync", DeviceGroupFilter.SameGroup, null)!;
        Require(filterChange.IsCompletedSuccessfully && filterChange.Result,
            "preview can save the default group filter without starting the background service");
        Require(snapshot.Settings.PeerGroupFilter == DeviceGroupFilter.SameGroup,
            "saving the default group filter updates the persisted settings model");
        Require(Visuals<TextBox>(panel).Contains(name) && Visuals<TextBox>(panel).Contains(folder) && Visuals<TextBox>(panel).Contains(group),
            "background discovery and group-filter changes do not replace the active general settings form");
        Require(name.Text == "尚未保存的设备名称" && folder.Text == @"D:\尚未保存的接收位置" && receiving.IsChecked == false && group.Text == "尚未保存的个人项目组",
            "device name, receive folder, receive toggle and custom group drafts survive metadata refresh and group-filter saves");
        Require(snapshot.Settings.DeviceName != name.Text && snapshot.Settings.ReceiveDirectory != folder.Text && snapshot.Settings.ReceivingEnabled && snapshot.Settings.GroupName != group.Text,
            "unsaved edits do not change the stored settings");
    }

    private static void UpdateSettingsStates(MainWindow window)
    {
        Scenario(window, "few");
        PopulateSettings(window);
        Call(window, "SetSidebarCollapsed", false);
        var snapshot = Read<DaemonSnapshot>(window, "_snapshot");
        foreach (string state in new[] { "downloading", "paused", "ready" })
        {
            snapshot.Update = new PeerUpdateStatus
            {
                State = state, CanUpdate = true, Version = "0.4.0", CanResume = state == "paused",
                PeerName = "设计工作站 / Design-Workstation-For-Shared-Release-Packages",
                BytesReceived = 73L * 1048576, TotalBytes = 128L * 1048576,
                ReusedBytes = 12L * 1048576, PeerBytes = 25L * 1048576, SourceCount = 3,
                Message = state switch
                {
                    "downloading" => "正在从附近设备下载并校验更新包，已有内容会继续保留。",
                    "paused" => "下载已暂停，重新选择同一版本即可继续。",
                    _ => "更新包已下载并通过完整性与发布签名验证。"
                }
            };
            OpenSettings(window, "updates");
            var root = Layout(window, minimum: true);
            AssertSettingsLayout(window, "updates", root);
            var panel = SettingsPanel(window, "updates");
            string label = state == "downloading" ? "暂停下载" : state == "paused" ? "继续下载" : "重启并更新";
            var action = SettingButtons(panel).Single(button => button.Content?.ToString() == label);
            Require(action.IsEnabled, $"{state} exposes its available action");
            Require(Texts(panel).Contains("0.4.0"), $"{state} identifies the selected update version");
            if (state == "downloading")
            {
                var progress = Visuals<ProgressBar>(panel).Single();
                Require(progress.Value == snapshot.Update.BytesReceived && progress.Maximum == snapshot.Update.TotalBytes,
                    "download progress shows actual received and total byte counts");
                Require(!progress.IsIndeterminate, "a known update size renders determinate progress");
                Within(progress, SettingsScroll(window, "updates"), "update progress");
            }
            SavePreview(window, $"settings-updates-{state}-minimum.png");
            if (state == "downloading")
            {
                snapshot.Update.BytesReceived = snapshot.Update.TotalBytes;
                snapshot.Update.Message = "正在验证完整发行包与所有文件…";
                Call(window, "RefreshUpdateUi");
                Layout(window, minimum: true);
                var phase = Visuals<TextBlock>(panel).SingleOrDefault(text => text.Text == snapshot.Update.Message);
                Require(phase != null && phase.Visibility == Visibility.Visible && FitsWithin(phase, SettingsScroll(window, "updates")),
                    "completed download bytes still show the active full-package verification phase");
                var progress = Visuals<ProgressBar>(panel).Single();
                Require(progress.Value == progress.Maximum, "verification phase retains the completed byte progress");
                SavePreview(window, "settings-updates-verifying-minimum.png");
            }
            if (state == "ready")
            {
                snapshot.Transfers.Add(new TransferProgress { Id = "preview-active", State = "sending", TotalBytes = 100 });
                Call(window, "RefreshUpdateUi");
                Layout(window, minimum: true);
                action = SettingButtons(panel).Single(button => button.Content?.ToString() == label);
                Require(!action.IsEnabled, "installation stays disabled while a file transfer is active");
                SavePreview(window, "settings-updates-ready-active-transfer.png");
                snapshot.Transfers.Clear();
                snapshot.Update.Message = "所选版本暂时无法下载；上次更新内容仍保留。连接失败。";
                Call(window, "RefreshUpdateUi");
                Layout(window, minimum: true);
                var failure = Visuals<TextBlock>(panel).SingleOrDefault(text => text.Text.Contains(snapshot.Update.Message));
                Require(failure != null && failure.Visibility == Visibility.Visible && FitsWithin(failure, SettingsScroll(window, "updates")),
                    "a failed new download remains visible while the previously ready update is retained");
                Require(SettingButtons(panel).Single(button => button.Content?.ToString() == label).IsEnabled,
                    "the previously verified update remains installable after the new download fails");
                SavePreview(window, "settings-updates-ready-download-failed-minimum.png");
            }
        }
    }

    private static void ReceiveTransferStates(MainWindow window)
    {
        string temporary = Path.Combine(Path.GetTempPath(), "Boardcast.LayoutTests-receive-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            File.WriteAllText(Path.Combine(temporary, "first-completed.txt"), "verified file one");
            File.WriteAllText(Path.Combine(temporary, "second-completed.txt"), "verified file two");
            Scenario(window, "few");
            var snapshot = Read<DaemonSnapshot>(window, "_snapshot");
            snapshot.Pending.Clear();
            snapshot.Transfers.Clear();
            foreach (string state in new[] { "cancelled", "failed" })
            {
                snapshot.Transfers.Clear();
                snapshot.Transfers.Add(new TransferProgress
                {
                    Id = "preview-receive-" + state, Direction = "receive", State = state,
                    PeerName = "设计工作站", Destination = temporary, CompletedFiles = 2,
                    BytesTransferred = 2048, TotalBytes = 4096, CanDiscard = state == "failed",
                    // Even a retained legacy flag must not offer the sender-only resume command.
                    CanResume = true, Message = state == "cancelled" ? "已取消，已保留 2 个完成文件。" : "传输中断，已保留 2 个完成文件。"
                });
                Call(window, "ShowPage", "transfers");
                Layout(window, minimum: true);
                var panel = Control<StackPanel>(window, "TransferContent");
                var scroll = Control<ScrollViewer>(window, "TransfersPage");
                var saved = Visuals<TextBlock>(panel).Single(text => text.Text == "已保存 2 个文件");
                Require(saved.Visibility == Visibility.Visible && FitsWithin(saved, scroll),
                    state + " shows the exact saved-file count inside the visible transfer page");
                Require(Texts(panel).Contains(state == "cancelled" ? "已取消" : "未完成"),
                    state + " retains its final transfer status");
                var open = Visuals<Button>(panel).Single(button => Equals(button.Content, "打开接收目录"));
                Require(open.IsEnabled, state + " can open the existing directory containing completed files");
                Within(open, scroll, state + " completed receive directory action");
                Require(!Visuals<Button>(panel).Any(button => Equals(button.Content, "继续传输")),
                    state + " never offers an outgoing resume command for a received transfer");
                var cleanup = Visuals<Button>(panel).Where(button => Equals(button.Content, "清理未完成内容")).ToList();
                Require(cleanup.Count == (state == "failed" ? 1 : 0),
                    state + " only offers cleanup while incomplete receive content remains");
                if (state == "failed")
                {
                    Require(cleanup[0].IsEnabled, "failed receive cleanup remains available");
                    Within(cleanup[0], scroll, "failed receive cleanup action");
                }
                SavePreview(window, "transfers-receive-" + state + "-minimum.png");
            }
        }
        finally
        {
            string full = Path.GetFullPath(temporary);
            string parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(parent, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(full).StartsWith("Boardcast.LayoutTests-receive-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected receive-layout cleanup path.");
            Directory.Delete(full, recursive: true);
        }
    }

    private static void PopulateSettings(MainWindow window, bool longContent = false)
    {
        var snapshot = Read<DaemonSnapshot>(window, "_snapshot");
        string suffix = longContent ? " · 跨设备协作归档与设计文件长期保存工作站" + new string('W', 54) : "";
        string path = longContent
            ? @"D:\团队共享资料\年度品牌计划\需要保留完整名称的设计文件与交付归档\客户版本确认\" + new string('p', 100) + @"\最终素材"
            : @"D:\邻间接收\设计协作";
        snapshot.Settings.DeviceName = "我的设计电脑" + suffix;
        snapshot.Settings.ReceiveDirectory = path;
        snapshot.Settings.Devices = Enumerable.Range(1, longContent ? 24 : 3).Select(index => new KnownDevice
        {
            Fingerprint = index.ToString("x64"), Name = $"工作站-{index:00}",
            Alias = (index == 1 ? "设计工作站" : index == 2 ? "客厅 MacBook" : $"协作电脑 {index:00}") + suffix,
            Trusted = index != 2, AutoAccept = index == 1, LastAddress = $"192.168.1.{index + 20}", Port = 45831
        }).ToList();
        snapshot.Settings.Rules = Enumerable.Range(1, longContent ? 24 : 3).Select(index => new ReceiveRule
        {
            Id = $"preview-rule-{index}", Name = (index == 1 ? "设计素材" : index == 2 ? "项目文件夹" : $"协作归档 {index:00}") + suffix,
            Enabled = index != 3, Scope = index == 2 ? EntryScope.Folders : EntryScope.Files,
            Extensions = index == 2 ? [] : [".png", ".jpg", ".pdf", ".svg"],
            FolderNamePattern = index == 2 ? "Design-*" : "", MaxBytes = 512L * 1048576,
            Destination = path + $@"\规则-{index:00}", AutoAccept = index == 1,
            DeviceFingerprint = index == 1 ? snapshot.Settings.Devices[0].Fingerprint : ""
        }).ToList();
        snapshot.Settings.Integrations = Enumerable.Range(1, longContent ? 24 : 2).Select(index => new IntegrationProfile
        {
            Id = $"preview-tool-{index}", Name = (index == 1 ? "本地内容摘要" : $"文档说明工具 {index:00}") + suffix,
            Enabled = index == 1, Executable = path + $@"\工具\summary-{index:00}.exe",
            Arguments = ["--format", "plain"], TimeoutSeconds = 30, IncludeLocalPaths = index == 1
        }).ToList();
        snapshot.Diagnostics = Enumerable.Range(1, longContent ? 8 : 2)
            .Select(index => $"诊断记录 {index:00} · " + (longContent
                ? "已启用局域网发现，当前网络接口可用于设备连接；接收文件会保存到 " + path
                : index == 1 ? "局域网发现正常，正在监听设备连接。" : "接收目录可用。"))
            .ToList();
        snapshot.Peers[0] = snapshot.Peers[0] with { AppVersion = "0.4.0", UpdateVersion = "0.4.0" };
        snapshot.Peers.Add(snapshot.Peers[0] with { Fingerprint = new string('e', 64), Name = "书房工作站", Address = "192.168.1.45", AppVersion = "0.3.1", UpdateVersion = "0.3.1" });
        snapshot.Update = new PeerUpdateStatus { State = "idle", CanUpdate = true, Message = "已发现附近设备提供的新版本。" };
    }

    private static void OpenSettings(MainWindow window, string section)
    {
        Call(window, "ShowPage", "settings");
        string name = section switch
        {
            "general" => "GeneralSettingsTab", "rules" => "RulesSettingsTab", "known" => "DevicesSettingsTab",
            "extensions" => "ExtensionsSettingsTab", _ => "UpdatesSettingsTab"
        };
        Control<Button>(window, name).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Require(Read<string>(window, "_settingsSection") == section, "clicking the settings tab selects the requested section");
        SettingsScroll(window, section).ScrollToTop();
    }

    private static Panel SettingsPanel(MainWindow window, string section) => Control<Panel>(window, section == "rules" ? "RuleContent" : "SettingsContent");
    private static ScrollViewer SettingsScroll(MainWindow window, string section) => Control<ScrollViewer>(window, section == "rules" ? "RulesPage" : "SettingsPage");
    private static IEnumerable<Button> SettingButtons(DependencyObject panel) => Visuals<Button>(panel)
        .Where(button => button.Visibility == Visibility.Visible && button.ActualHeight > 0);

    private static void AssertSettingsLayout(MainWindow window, string section, FrameworkElement root)
    {
        var scroll = SettingsScroll(window, section);
        var panel = SettingsPanel(window, section);
        Require(scroll.Visibility == Visibility.Visible, "the selected settings content is visible");
        Within(scroll, root, "settings viewport");
        Require(scroll.ScrollableWidth <= 1, "settings do not need horizontal scrolling");
        Require(panel.ActualWidth > 300 && panel.ActualWidth <= scroll.ViewportWidth + 1,
            "settings retain a readable content width inside the viewport");
        var tabs = new[] { "GeneralSettingsTab", "RulesSettingsTab", "DevicesSettingsTab", "ExtensionsSettingsTab", "UpdatesSettingsTab" }
            .Select(tab => Control<Button>(window, tab)).ToArray();
        Require(tabs.All(tab => FitsWithin(tab, root)), "all five settings tabs remain visible within the window");
        var buttons = SettingButtons(panel).ToArray();
        Require(buttons.All(button => button.ActualHeight <= 36), "settings actions use compact heights");
        Require(buttons.All(button => button.ActualWidth <= 240 && button.ActualWidth < panel.ActualWidth * .7),
            "settings actions fit their labels instead of stretching into wide bars");
        Require(buttons.All(button => FitsHorizontally(button, panel)), "all settings row actions stay within the content width");
        Require(Visuals<TextBox>(panel).All(input => FitsHorizontally(input, panel)), "settings inputs stay within the content width");
    }

    private static bool FitsHorizontally(FrameworkElement element, FrameworkElement root)
    {
        Rect bounds = element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));
        return element.ActualWidth > 0 && bounds.Left >= -1 && bounds.Right <= root.ActualWidth + 1;
    }

    private static bool FitsWithin(FrameworkElement element, FrameworkElement root)
    {
        Rect bounds = element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));
        return element.ActualWidth > 0 && element.ActualHeight > 0 && bounds.Left >= -1 && bounds.Top >= -1 &&
            bounds.Right <= root.ActualWidth + 1 && bounds.Bottom <= root.ActualHeight + 1;
    }

    private static void SavePreview(Window window, string name)
    {
        if (PreviewDirectory == null) return;
        var root = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        // Content-only rendering does not include Window's background; paint it
        // first so standalone editor previews match their on-screen appearance.
        var background = new DrawingVisual();
        using (var drawing = background.RenderOpen())
            drawing.DrawRectangle(window.Background ?? Brushes.White, null, new Rect(root.RenderSize));
        bitmap.Render(background);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(PreviewDirectory);
        using var stream = File.Create(Path.Combine(PreviewDirectory, name));
        encoder.Save(stream);
    }

    private static string Texts(DependencyObject node) => string.Join("\n", Visuals<TextBlock>(node).Select(t => t.Text));
    private static PeerInfo Peer(object row) => (PeerInfo)row.GetType().GetProperty("Peer")!.GetValue(row)!;
    private static List<PeerInfo> Recipients(MainWindow window) => (List<PeerInfo>)Call(window, "SelectedRecipients")!;
    private static T Control<T>(MainWindow window, string name) where T : DependencyObject =>
        window.FindName(name) is T control ? control : throw new InvalidOperationException("Missing control " + name);
    private static T Read<T>(MainWindow window, string name) =>
        (T)(typeof(MainWindow).GetField(name, Hidden)?.GetValue(window) ??
            (default(T) is null ? default(T)! : throw new InvalidOperationException("Missing field " + name)));
    private static void Set(MainWindow window, string name, object value) =>
        (typeof(MainWindow).GetField(name, Hidden) ?? throw new InvalidOperationException("Missing field " + name)).SetValue(window, value);
    private static object? Call(MainWindow window, string name, params object?[] args) =>
        (typeof(MainWindow).GetMethod(name, Hidden) ?? throw new InvalidOperationException("Missing method " + name)).Invoke(window, args);

    private static IEnumerable<T> Visuals<T>(DependencyObject node) where T : DependencyObject
    {
        if (node is T found) yield return found;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            foreach (T child in Visuals<T>(VisualTreeHelper.GetChild(node, i))) yield return child;
    }

    private static void Within(FrameworkElement element, FrameworkElement root, string name)
    {
        Require(element.ActualWidth > 0 && element.ActualHeight > 0, name + " has a real arranged size");
        Rect bounds = element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));
        Require(bounds.Left >= -1 && bounds.Top >= -1 && bounds.Right <= root.ActualWidth + 1 && bounds.Bottom <= root.ActualHeight + 1,
            $"{name} stays inside client area; bounds={bounds}, client={root.ActualWidth}x{root.ActualHeight}");
    }

    private static void Require(bool condition, string message)
    {
        checks++;
        if (!condition) throw new InvalidOperationException(message);
    }
}

