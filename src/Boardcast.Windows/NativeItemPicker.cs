using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace Boardcast.Windows;

internal static class NativeItemPicker
{
    internal static IReadOnlyList<string> ShowFiles(Window owner, string? initialDirectory = null, string title = "添加文件")
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            Multiselect = true,
            CheckFileExists = true,
            InitialDirectory = ExistingDirectory(initialDirectory)
        };
        return dialog.ShowDialog(owner) == true ? dialog.FileNames : [];
    }

    internal static IReadOnlyList<string> ShowFolders(Window owner, string? initialDirectory = null, string title = "添加文件夹")
    {
        var dialog = new OpenFolderDialog
        {
            Title = title,
            Multiselect = true,
            InitialDirectory = ExistingDirectory(initialDirectory)
        };
        return dialog.ShowDialog(owner) == true ? dialog.FolderNames : [];
    }

    private static string ExistingDirectory(string? directory) =>
        directory is not null && Directory.Exists(directory) ? directory : "";
}
