using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Boardcast.Core;

namespace Boardcast.Windows;

internal sealed class ExclusionsDialog : EditorDialog
{
    private readonly string _root;
    private readonly TextBox _paths;
    private readonly TextBlock _error = new() { Foreground = Brushes.Firebrick };
    public IReadOnlyList<string> ExcludedPaths { get; private set; } = [];

    public ExclusionsDialog(Window owner, string root, IReadOnlyList<string> excludedPaths)
        : base(owner, "排除项 · " + Path.GetFileName(Path.TrimEndingDirectorySeparator(root)))
    {
        _root = root; Width = 570; Height = 470;
        AutomationProperties.SetAutomationId(this, "ExclusionsDialog");
        AddNote("相对路径，每行一项；排除目录会排除其全部内容。");
        var pickers = new WrapPanel { Margin = new Thickness(0, 0, 0, 14) };
        var files = new Button { Content = "添加文件…" };
        var folders = new Button { Content = "添加文件夹…" };
        files.Click += (_, _) => AddAbsolutePaths(NativeItemPicker.ShowFiles(this, _root, "选择要排除的文件"));
        folders.Click += (_, _) => AddAbsolutePaths(NativeItemPicker.ShowFolders(this, _root, "选择要排除的文件夹"));
        pickers.Children.Add(files); pickers.Children.Add(folders); _fields.Children.Add(pickers);
        _paths = AddText("排除路径", string.Join(Environment.NewLine, excludedPaths), multiline: true);
        _paths.Height = 170; _paths.TextWrapping = TextWrapping.NoWrap;
        _paths.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        _paths.VerticalContentAlignment = VerticalAlignment.Top;
        AutomationProperties.SetAutomationId(_paths, "ExcludedPaths");
        _fields.Children.Add(_error);
    }

    private string[] Lines() => _paths.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    protected override bool TryAccept()
    {
        try { ExcludedPaths = TransferExclusions.NormalizeRelativePaths(Lines()); return true; }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException) { _error.Text = ex.Message; return false; }
    }
    private void AddAbsolutePaths(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        try
        {
            var relative = TransferExclusions.RelativePaths(_root, paths);
            _paths.Text = string.Join(Environment.NewLine, TransferExclusions.NormalizeRelativePaths(Lines().Concat(relative)));
            _error.Text = "";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException) { _error.Text = ex.Message; }
    }
}