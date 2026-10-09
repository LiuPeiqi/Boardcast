using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using Boardcast.Core;

namespace Boardcast.Windows;

/// <summary>Each scan has its own cancellation token so stale results cannot replace new selections.</summary>
internal sealed class SelectedPath : INotifyPropertyChanged, IDisposable
{
    private CancellationTokenSource? _cancellation;
    private readonly CancellationToken _lifetime;
    private string _sizeText;
    private bool _isCompact;
    public bool IsCompact { get => _isCompact; set { if (_isCompact == value) return; _isCompact = value; Changed(); } }
    public string Path { get; }
    public bool IsDirectory { get; }
    public bool IsRemoved { get; private set; }
    public CancellationToken CancellationToken { get; private set; }
    public string Name { get; }
    public ImageSource Icon => FileItemIcon.Get(Path, IsDirectory);
    public IReadOnlyList<string> ExcludedPaths { get; private set; } = [];
    public string ExclusionText => ExcludedPaths.Count == 0 ? "" : $"已排除 {ExcludedPaths.Count} 项";
    public string AutomationLabel => Name + " · " + SizeText + (ExcludedPaths.Count == 0 ? "" : " · " + ExclusionText);
    public string SizeText
    {
        get => _sizeText;
        set
        {
            if (IsRemoved || _sizeText == value) return;
            _sizeText = value;
            Changed();
            Changed(nameof(AutomationLabel));
        }
    }

    public SelectedPath(string path, bool isDirectory, string sizeText, CancellationToken lifetime)
    {
        Path = path; IsDirectory = isDirectory; _sizeText = sizeText; _lifetime = lifetime;
        Name = System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(path));
        if (isDirectory)
        {
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            CancellationToken = _cancellation.Token;
        }
    }

    public void SetExclusions(IEnumerable<string> paths)
    {
        if (!IsDirectory || IsRemoved) return;
        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
        CancellationToken = _cancellation.Token;
        ExcludedPaths = paths.ToArray();
        SizeText = "正在统计…";
        Changed(nameof(ExclusionText));
        Changed(nameof(AutomationLabel));
    }

    public TransferSource ToSource() => new() { Path = Path, ExcludedPaths = ExcludedPaths.ToList() };
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    public void Dispose()
    {
        if (IsRemoved) return;
        IsRemoved = true;
        _cancellation?.Cancel();
        _cancellation?.Dispose();
    }
}
