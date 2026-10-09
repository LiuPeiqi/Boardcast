using System.Windows;
using System.Windows.Media;

namespace Boardcast.Windows;

/// <summary>Small, frozen WPF vectors; classification never opens a local or incoming path.</summary>
internal static class FileItemIcon
{
    // Adapted from Material Icon Theme (MIT); see docs/third-party/Material-Icon-Theme.LICENSE.
    // The neutral file and warm folder reuse the upstream document and folder-base geometry.
    private static readonly IReadOnlyDictionary<string, DrawingImage> Icons = CreateIcons();
    private static readonly IReadOnlyDictionary<string, string> Extensions = CreateExtensions();

    public static ImageSource Get(string path, bool isDirectory)
    {
        if (isDirectory) return Icons["folder"];
        string name = path[(path.LastIndexOfAny(['/', '\\']) + 1)..];
        string normalizedName = name.ToLowerInvariant();
        string? namedIcon = normalizedName switch
        {
            ".gitignore" or ".gitattributes" or ".gitmodules" or ".gitkeep" => "git",
            "dockerfile" or "makefile" or "cmakelists.txt" or ".editorconfig" or ".env" => "settings",
            "readme" or "license" or "licence" or "copying" => "document",
            _ => null
        };
        if (namedIcon is not null) return Icons[namedIcon];
        int dot = name.LastIndexOf('.');
        return dot >= 0 && Extensions.TryGetValue(name[dot..], out string? icon)
            ? Icons[icon] : Icons["file"];
    }

    private static IReadOnlyDictionary<string, string> CreateExtensions()
    {
        var icons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Add(string icon, params string[] extensions)
        {
            foreach (string extension in extensions) icons.Add(extension, icon);
        }
        Add("image", ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".tif", ".tiff", ".ico", ".svg", ".heic", ".avif", ".psd");
        Add("pdf", ".pdf");
        Add("zip", ".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".bz2", ".xz", ".zst", ".iso");
        Add("audio", ".mp3", ".wav", ".flac", ".aac", ".m4a", ".ogg", ".opus", ".wma", ".aiff", ".mid", ".midi");
        Add("video", ".mp4", ".mkv", ".mov", ".avi", ".webm", ".wmv", ".m4v", ".mpeg", ".mpg");
        Add("document", ".txt", ".log", ".rtf", ".odt", ".epub");
        Add("word", ".doc", ".docx", ".docm", ".dot", ".dotx");
        Add("table", ".xls", ".xlsx", ".xlsm", ".ods", ".csv", ".tsv");
        Add("powerpoint", ".ppt", ".pptx", ".pptm", ".odp");
        Add("markdown", ".md", ".markdown", ".rst");
        Add("json", ".json", ".jsonc", ".json5", ".jsonl", ".ipynb");
        Add("settings", ".yaml", ".yml", ".toml", ".ini", ".cfg", ".conf", ".config", ".props", ".targets", ".csproj", ".sln", ".slnx", ".env");
        Add("xml", ".xml", ".xaml", ".vue", ".php", ".rb", ".swift", ".kt", ".kts", ".dart", ".lua");
        Add("javascript", ".js", ".mjs", ".cjs", ".jsx");
        Add("typescript", ".ts", ".tsx", ".mts", ".cts");
        Add("python", ".py", ".pyw", ".pyi");
        Add("csharp", ".cs", ".csx");
        Add("c", ".c", ".h");
        Add("cpp", ".cpp", ".hpp", ".cc", ".cxx", ".hxx");
        Add("rust", ".rs");
        Add("java", ".java", ".jar", ".class");
        Add("go", ".go");
        Add("html", ".html", ".htm", ".xhtml");
        Add("css", ".css", ".scss", ".sass", ".less");
        Add("git", ".patch", ".diff");
        Add("console", ".ps1", ".psm1", ".psd1", ".bat", ".cmd", ".sh", ".bash", ".zsh");
        Add("database", ".sql", ".db", ".sqlite", ".sqlite3", ".mdb");
        Add("lock", ".pem", ".key", ".crt", ".cer", ".pfx", ".p12", ".lock");
        Add("exe", ".exe", ".msi", ".msix", ".appx", ".dll");
        Add("font", ".ttf", ".otf", ".woff", ".woff2");
        return icons;
    }

    private static IReadOnlyDictionary<string, DrawingImage> CreateIcons() => new Dictionary<string, DrawingImage>
    {
        ["folder"] = Create(0, 0, 16, 16,
            ("#E3AC45", "m6.922 3.768-.644-.536A1 1 0 0 0 5.638 3H2a1 1 0 0 0-1 1v8a1 1 0 0 0 1 1h12a1 1 0 0 0 1-1V5a1 1 0 0 0-1-1H7.562a1 1 0 0 1-.64-.232")),
        ["file"] = Create(0, 0, 24, 24,
            ("#8A96A4", "M8 16h8v2H8zm0-4h8v2H8zm6-10H6c-1.1 0-2 .9-2 2v16c0 1.1.89 2 1.99 2H18c1.1 0 2-.9 2-2V8zm4 18H6V4h7v5h5z")),
        ["image"] = Create(0, 0, 16, 16,
            ("#26a69a", "M8.5 6h4l-4-4zM3.875 1H9.5l4 4v8.6c0 .773-.616 1.4-1.375 1.4h-8.25c-.76 0-1.375-.627-1.375-1.4V2.4c0-.777.612-1.4 1.375-1.4M4 13.6h8V8l-2.625 2.8L8 9.4zm1.25-7.7c-.76 0-1.375.627-1.375 1.4s.616 1.4 1.375 1.4c.76 0 1.375-.627 1.375-1.4S6.009 5.9 5.25 5.9")),
        ["pdf"] = Create(0, 0, 24, 24,
            ("#ef5350", "M13 9h5.5L13 3.5zM6 2h8l6 6v12a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2m4.93 10.44c.41.9.93 1.64 1.53 2.15l.41.32c-.87.16-2.07.44-3.34.93l-.11.04.5-1.04c.45-.87.78-1.66 1.01-2.4m6.48 3.81c.18-.18.27-.41.28-.66.03-.2-.02-.39-.12-.55-.29-.47-1.04-.69-2.28-.69l-1.29.07-.87-.58c-.63-.52-1.2-1.43-1.6-2.56l.04-.14c.33-1.33.64-2.94-.02-3.6a.85.85 0 0 0-.61-.24h-.24c-.37 0-.7.39-.79.77-.37 1.33-.15 2.06.22 3.27v.01c-.25.88-.57 1.9-1.08 2.93l-.96 1.8-.89.49c-1.2.75-1.77 1.59-1.88 2.12-.04.19-.02.36.05.54l.03.05.48.31.44.11c.81 0 1.73-.95 2.97-3.07l.18-.07c1.03-.33 2.31-.56 4.03-.75 1.03.51 2.24.74 3 .74.44 0 .74-.11.91-.3m-.41-.71.09.11c-.01.1-.04.11-.09.13h-.04l-.19.02c-.46 0-1.17-.19-1.9-.51.09-.1.13-.1.23-.1 1.4 0 1.8.25 1.9.35M7.83 17c-.65 1.19-1.24 1.85-1.69 2 .05-.38.5-1.04 1.21-1.69zm3.02-6.91c-.23-.9-.24-1.63-.07-2.05l.07-.12.15.05c.17.24.19.56.09 1.1l-.03.16-.16.82z")),
        ["zip"] = Create(0, 0, 24, 24,
            ("#afb42b", "M14 17h-2v-2h-2v-2h2v2h2m0-6h-2v2h2v2h-2v-2h-2V9h2V7h-2V5h2v2h2m5-4H5c-1.11 0-2 .89-2 2v14a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2V5a2 2 0 0 0-2-2")),
        ["audio"] = Create(0, 0, 32, 32,
            ("#ef5350", "M16 2a14 14 0 1 0 14 14A14 14 0 0 0 16 2m6 10h-4v8a4 4 0 1 1-4-4 3.96 3.96 0 0 1 2 .555V8h6Z")),
        ["video"] = Create(0, 0, 32, 32,
            ("#ff9800", "m24 6 2 6h-4l-2-6h-3l2 6h-4l-2-6h-3l2 6H8L6 6H5a3 3 0 0 0-3 3v14a3 3 0 0 0 3 3h22a3 3 0 0 0 3-3V6Z")),
        ["document"] = Create(0, 0, 24, 24,
            ("#42a5f5", "M8 16h8v2H8zm0-4h8v2H8zm6-10H6c-1.1 0-2 .9-2 2v16c0 1.1.89 2 1.99 2H18c1.1 0 2-.9 2-2V8zm4 18H6V4h7v5h5z")),
        ["word"] = Create(0, 0, 24, 24,
            ("#01579b", "M6 2h8l6 6v12a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2m7 1.5V9h5.5zM7 13l1.5 7h2l1.5-3 1.5 3h2l1.5-7h1v-2h-4v2h1l-.9 4.2L13 15h-2l-1.1 2.2L9 13h1v-2H6v2z")),
        ["table"] = Create(0, 0, 24, 24,
            ("#8bc34a", "M6 2h8l6 6v12a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2m7 1.5V9h5.5zm4 7.5h-4v2h1l-2 1.67L10 13h1v-2H7v2h1l3 2.5L8 18H7v2h4v-2h-1l2-1.67L14 18h-1v2h4v-2h-1l-3-2.5 3-2.5h1z")),
        ["powerpoint"] = Create(0, 0, 24, 24,
            ("#e64a19", "M6 2h8l6 6v12a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2m7 1.5V9h5.5zM8 11v2h1v6H8v1h4v-1h-1v-2h2a3 3 0 0 0 3-3 3 3 0 0 0-3-3zm5 2a1 1 0 0 1 1 1 1 1 0 0 1-1 1h-2v-2z")),
        ["markdown"] = Create(0, 0, 32, 32,
            ("#42a5f5", "m14 10-4 3.5L6 10H4v12h4v-6l2 2 2-2v6h4V10zm12 6v-6h-4v6h-4l6 8 6-8z")),
        ["json"] = Create(0, -960, 960, 960,
            ("#f9a825", "M560-160v-80h120q17 0 28.5-11.5T720-280v-80q0-38 22-69t58-44v-14q-36-13-58-44t-22-69v-80q0-17-11.5-28.5T680-720H560v-80h120q50 0 85 35t35 85v80q0 17 11.5 28.5T840-560h40v160h-40q-17 0-28.5 11.5T800-360v80q0 50-35 85t-85 35zm-280 0q-50 0-85-35t-35-85v-80q0-17-11.5-28.5T120-400H80v-160h40q17 0 28.5-11.5T160-600v-80q0-50 35-85t85-35h120v80H280q-17 0-28.5 11.5T240-680v80q0 38-22 69t-58 44v14q36 13 58 44t22 69v80q0 17 11.5 28.5T280-240h120v80z")),
        ["settings"] = Create(0, 0, 24, 24,
            ("#42a5f5", "M19.43 12.98c.04-.32.07-.64.07-.98s-.03-.66-.07-.98l2.11-1.65c.19-.15.24-.42.12-.64l-2-3.46a.5.5 0 0 0-.61-.22l-2.49 1c-.52-.4-1.08-.73-1.69-.98l-.38-2.65A.49.49 0 0 0 14 2h-4c-.25 0-.46.18-.49.42l-.38 2.65c-.61.25-1.17.59-1.69.98l-2.49-1a.6.6 0 0 0-.18-.03c-.17 0-.34.09-.43.25l-2 3.46c-.13.22-.07.49.12.64l2.11 1.65c-.04.32-.07.65-.07.98s.03.66.07.98l-2.11 1.65c-.19.15-.24.42-.12.64l2 3.46a.5.5 0 0 0 .61.22l2.49-1c.52.4 1.08.73 1.69.98l.38 2.65c.03.24.24.42.49.42h4c.25 0 .46-.18.49-.42l.38-2.65c.61-.25 1.17-.59 1.69-.98l2.49 1q.09.03.18.03c.17 0 .34-.09.43-.25l2-3.46c.12-.22.07-.49-.12-.64zm-1.98-1.71c.04.31.05.52.05.73s-.02.43-.05.73l-.14 1.13.89.7 1.08.84-.7 1.21-1.27-.51-1.04-.42-.9.68c-.43.32-.84.56-1.25.73l-1.06.43-.16 1.13-.2 1.35h-1.4l-.19-1.35-.16-1.13-1.06-.43c-.43-.18-.83-.41-1.23-.71l-.91-.7-1.06.43-1.27.51-.7-1.21 1.08-.84.89-.7-.14-1.13c-.03-.31-.05-.54-.05-.74s.02-.43.05-.73l.14-1.13-.89-.7-1.08-.84.7-1.21 1.27.51 1.04.42.9-.68c.43-.32.84-.56 1.25-.73l1.06-.43.16-1.13.2-1.35h1.39l.19 1.35.16 1.13 1.06.43c.43.18.83.41 1.23.71l.91.7 1.06-.43 1.27-.51.7 1.21-1.07.85-.89.7zM12 8c-2.21 0-4 1.79-4 4s1.79 4 4 4 4-1.79 4-4-1.79-4-4-4m0 6c-1.1 0-2-.9-2-2s.9-2 2-2 2 .9 2 2-.9 2-2 2")),
        ["xml"] = Create(0, 0, 24, 24,
            ("#8bc34a", "M13 9h5.5L13 3.5zM6 2h8l6 6v12a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2V4c0-1.11.89-2 2-2m.12 13.5 3.74 3.74 1.42-1.41-2.33-2.33 2.33-2.33-1.42-1.41zm11.16 0-3.74-3.74-1.42 1.41 2.33 2.33-2.33 2.33 1.42 1.41z")),
        ["javascript"] = Create(0, 0, 16, 16,
            ("#ffca28", "M2 2v12h12V2zm6 6h1v4a1.003 1.003 0 0 1-1 1H7a1.003 1.003 0 0 1-1-1v-1h1v1h1zm3 0h2v1h-2v1h1a1.003 1.003 0 0 1 1 1v1a1.003 1.003 0 0 1-1 1h-2v-1h2v-1h-1a1.003 1.003 0 0 1-1-1V9a1.003 1.003 0 0 1 1-1")),
        ["typescript"] = Create(0, 0, 16, 16,
            ("#0288d1", "M2 2v12h12V2zm4 6h3v1H8v4H7V9H6zm5 0h2v1h-2v1h1a1.003 1.003 0 0 1 1 1v1a1.003 1.003 0 0 1-1 1h-2v-1h2v-1h-1a1.003 1.003 0 0 1-1-1V9a1.003 1.003 0 0 1 1-1")),
        ["python"] = Create(0, 0, 24, 24,
            ("#0288d1", "M9.86 2A2.86 2.86 0 0 0 7 4.86v1.68h4.29c.39 0 .71.57.71.96H4.86A2.86 2.86 0 0 0 2 10.36v3.781a2.86 2.86 0 0 0 2.86 2.86h1.18v-2.68a2.85 2.85 0 0 1 2.85-2.86h5.25c1.58 0 2.86-1.271 2.86-2.851V4.86A2.86 2.86 0 0 0 14.14 2zm-.72 1.61c.4 0 .72.12.72.71s-.32.891-.72.891c-.39 0-.71-.3-.71-.89s.32-.711.71-.711"),
            ("#fdd835", "M17.959 7v2.68a2.85 2.85 0 0 1-2.85 2.859H9.86A2.85 2.85 0 0 0 7 15.389v3.75a2.86 2.86 0 0 0 2.86 2.86h4.28A2.86 2.86 0 0 0 17 19.14v-1.68h-4.291c-.39 0-.709-.57-.709-.96h7.14A2.86 2.86 0 0 0 22 13.64V9.86A2.86 2.86 0 0 0 19.14 7zM8.32 11.513l-.004.004.038-.004zm6.54 7.276c.39 0 .71.3.71.89a.71.71 0 0 1-.71.71c-.4 0-.72-.12-.72-.71s.32-.89.72-.89")),
        ["csharp"] = Create(0, 0, 32, 32,
            ("#0288d1", "M30 14v-2h-2V8h-2v4h-2V8h-2v4h-2v2h2v2h-2v2h2v4h2v-4h2v4h2v-4h2v-2h-2v-2Zm-4 2h-2v-2h2Zm-12.437 6A5.57 5.57 0 0 1 8 16.437v-2.873A5.57 5.57 0 0 1 13.563 8H18V2h-4.437A11.563 11.563 0 0 0 2 13.563v2.873A11.564 11.564 0 0 0 13.563 28H18v-6Z")),
        ["c"] = Create(0, 0, 32, 32,
            ("#0288d1", "M19.563 22A5.57 5.57 0 0 1 14 16.437v-2.873A5.57 5.57 0 0 1 19.563 8H24V2h-4.437A11.563 11.563 0 0 0 8 13.563v2.873A11.564 11.564 0 0 0 19.563 28H24v-6Z")),
        ["cpp"] = Create(0, 0, 32, 32,
            ("#0288d1", "M28 14v-4h-2v4h-6v-4h-2v4h-4v2h4v4h2v-4h6v4h2v-4h4v-2z"),
            ("#0288d1", "M13.563 22A5.57 5.57 0 0 1 8 16.437v-2.873A5.57 5.57 0 0 1 13.563 8H18V2h-4.437A11.563 11.563 0 0 0 2 13.563v2.873A11.564 11.564 0 0 0 13.563 28H18v-6Z")),
        ["rust"] = Create(0, 0, 32, 32,
            ("#ff7043", "m30 12-4-2V6h-4l-2-4-4 2-4-2-2 4H6v4l-4 2 2 4-2 4 4 2v4h4l2 4 4-2 4 2 2-4h4v-4l4-2-2-4ZM6 16a9.9 9.9 0 0 1 .842-4H10v8H6.842A9.9 9.9 0 0 1 6 16m10 10a9.98 9.98 0 0 1-7.978-4H16v-2h-2v-2h4c.819.819.297 2.308 1.179 3.37a1.89 1.89 0 0 0 1.46.63h3.34A9.98 9.98 0 0 1 16 26m-2-12v-2h4a1 1 0 0 1 0 2Zm11.158 6H24a2.006 2.006 0 0 1-2-2 2 2 0 0 0-2-2 3 3 0 0 0 3-3q0-.08-.004-.161A3.115 3.115 0 0 0 19.83 10H8.022a9.986 9.986 0 0 1 17.136 10")),
        ["java"] = Create(0, 0, 32, 32,
            ("#f44336", "M4 26h24v2H4zM28 4H7a1 1 0 0 0-1 1v13a4 4 0 0 0 4 4h10a4 4 0 0 0 4-4v-4h4a2 2 0 0 0 2-2V6a2 2 0 0 0-2-2m0 8h-4V6h4Z")),
        ["go"] = Create(0, 0, 32, 32,
            ("#00acc1", "M2 12h4v2H2zm-2 4h6v2H0zm4 4h2v2H4zm16.954-5H14v3h3.239a4.42 4.42 0 0 1-3.531 2 2.65 2.65 0 0 1-2.053-.858 2.86 2.86 0 0 1-.628-2.28A4.515 4.515 0 0 1 15.292 13a2.73 2.73 0 0 1 1.749.584l2.962-1.185A5.6 5.6 0 0 0 15.292 10a7.526 7.526 0 0 0-7.243 6.5 5.614 5.614 0 0 0 5.659 6.5 7.526 7.526 0 0 0 7.243-6.5 6.4 6.4 0 0 0 .003-1.5"),
            ("#00acc1", "M26.292 10a7.526 7.526 0 0 0-7.243 6.5 5.614 5.614 0 0 0 5.659 6.5 7.526 7.526 0 0 0 7.243-6.5 5.614 5.614 0 0 0-5.659-6.5m2.681 6.137A4.515 4.515 0 0 1 24.708 20a2.65 2.65 0 0 1-2.053-.858 2.86 2.86 0 0 1-.628-2.28A4.515 4.515 0 0 1 26.292 13a2.65 2.65 0 0 1 2.053.858 2.86 2.86 0 0 1 .628 2.28Z")),
        ["html"] = Create(0, 0, 32, 32,
            ("#e65100", "m4 4 2 22 10 2 10-2 2-22Zm19.72 7H11.28l.29 3h11.86l-.802 9.335L15.99 25l-6.635-1.646L8.93 19h3.02l.19 2 3.86.77 3.84-.77.29-4H8.84L8 8h16Z")),
        ["css"] = Create(0, 0, 32, 32,
            ("#7e57c2", "M20 18h-2v-2h-2v2c0 .193 0 .703 1.254 1.033A3.345 3.345 0 0 1 20 22h2v2h2v-2c0-.388-.562-.851-1.254-1.034C20.356 20.34 20 18.84 20 18m-3.254 2.966C14.356 20.34 14 18.84 14 18h-2v-2h-2v8h2v-2h4v2h2v-2c0-.388-.562-.851-1.254-1.034"),
            ("#7e57c2", "M24 4H4v20a4 4 0 0 0 4 4h16.16A3.84 3.84 0 0 0 28 24.16V8a4 4 0 0 0-4-4m2 14h-2v-2h-2v2c0 .193 0 .703 1.254 1.033A3.345 3.345 0 0 1 26 22v2a2 2 0 0 1-2 2h-2a2 2 0 0 1-2-2 2 2 0 0 1-2 2h-2a2 2 0 0 1-2-2 2 2 0 0 1-2 2h-2a2 2 0 0 1-2-2v-8a2 2 0 0 1 2-2h2a2 2 0 0 1 2 2 2 2 0 0 1 2-2h2a2 2 0 0 1 2 2 2 2 0 0 1 2-2h2a2 2 0 0 1 2 2Z")),
        ["git"] = Create(0, 0, 32, 32,
            ("#e64a19", "M13.172 2.828 11.78 4.22l1.91 1.91 2 2A2.986 2.986 0 0 1 20 10.81a3.25 3.25 0 0 1-.31 1.31l2.06 2a2.68 2.68 0 0 1 3.37.57 2.86 2.86 0 0 1 .88 2.117 3.02 3.02 0 0 1-.856 2.109A2.9 2.9 0 0 1 23 19.81a2.93 2.93 0 0 1-2.13-.87 2.694 2.694 0 0 1-.56-3.38l-2-2.06a3 3 0 0 1-.31.12V20a3 3 0 0 1 1.44 1.09 2.92 2.92 0 0 1 .56 1.72 2.88 2.88 0 0 1-.878 2.128 2.98 2.98 0 0 1-2.048.871 2.981 2.981 0 0 1-2.514-4.719A3 3 0 0 1 16 20v-6.38a2.96 2.96 0 0 1-1.44-1.09 2.9 2.9 0 0 1-.56-1.72 2.9 2.9 0 0 1 .31-1.31l-3.9-3.9-7.579 7.572a4 4 0 0 0-.001 5.658l10.342 10.342a4 4 0 0 0 5.656 0l10.344-10.344a4 4 0 0 0 0-5.656L18.828 2.828a4 4 0 0 0-5.656 0")),
        ["console"] = Create(0, 0, 16, 16,
            ("#ff7043", "M2 2a1 1 0 0 0-1 1v10c0 .554.446 1 1 1h12c.554 0 1-.446 1-1V3a1 1 0 0 0-1-1zm0 3h12v8H2zm1 2 2 2-2 2 1 1 3-3-3-3zm5 3.5V12h5v-1.5z")),
        ["database"] = Create(0, 0, 32, 32,
            ("#ffca28", "M16 24c-5.525 0-10-.9-10-2v4c0 1.1 4.475 2 10 2s10-.9 10-2v-4c0 1.1-4.475 2-10 2m0-8c-5.525 0-10-.9-10-2v4c0 1.1 4.475 2 10 2s10-.9 10-2v-4c0 1.1-4.475 2-10 2m0-12C10.477 4 6 4.895 6 6v4c0 1.1 4.475 2 10 2s10-.9 10-2V6c0-1.105-4.477-2-10-2")),
        ["lock"] = Create(0, 0, 32, 32,
            ("#ffd54f", "M25 12h-3V8a6 6 0 0 0-12 0v4H7a1 1 0 0 0-1 1v16a1 1 0 0 0 1 1h18a1 1 0 0 0 1-1V13a1 1 0 0 0-1-1M14 8a2 2 0 0 1 4 0v4h-4Zm2 17a4 4 0 1 1 4-4 4 4 0 0 1-4 4")),
        ["exe"] = Create(0, 0, 32, 32,
            ("#e64a19", "M28 4H4a2 2 0 0 0-2 2v20a2 2 0 0 0 2 2h24a2 2 0 0 0 2-2V6a2 2 0 0 0-2-2m0 22H4V10h24Z")),
        ["font"] = Create(0, 0, 32, 32,
            ("#f44336", "M24 28h4L18 4h-4L4 28h4l8-19.422"),
            ("#f44336", "M8 20h16v4H8z"))
    };

    private static DrawingImage Create(double x, double y, double width, double height, params (string Color, string Path)[] layers)
    {
        double scaleX = 24 / width, scaleY = 24 / height;
        var shapes = new DrawingGroup { Transform = new MatrixTransform(scaleX, 0, 0, scaleY, -x * scaleX, -y * scaleY) };
        foreach (var layer in layers)
        {
            // SVG uses nonzero winding by default; WPF's path markup defaults to even-odd.
            var geometry = Geometry.Parse("F1 " + layer.Path);
            var color = (Color)ColorConverter.ConvertFromString(layer.Color);
            shapes.Children.Add(new GeometryDrawing(new SolidColorBrush(color), null, geometry));
        }
        var drawing = new DrawingGroup { ClipGeometry = new RectangleGeometry(new Rect(0, 0, 24, 24)) };
        drawing.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 24, 24))));
        drawing.Children.Add(shapes);
        var image = new DrawingImage(drawing);
        image.Freeze();
        return image;
    }
}
