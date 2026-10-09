using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Boardcast.Windows;

internal static class Program
{
    private sealed record Asset(string Id, string File, string Source, string Variant, string[] Colors,
        int Width, int Height, int TransparentPixels, int PaintedPixels, string Sha256);
    [STAThread]
    private static void Main(string[] args)
    {
        string output = args[0], repo = args[1];
        int size = int.Parse(args[2]);
        string version = args[3];
        string work = Path.Combine(repo, "artifacts", "mac-icon-design-export");
        string platformSource = File.ReadAllText(Path.Combine(work, "PlatformSource.txt"));
        var icons = (IReadOnlyDictionary<string, DrawingImage>)typeof(FileItemIcon)
            .GetField("Icons", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var extensions = (IReadOnlyDictionary<string, string>)typeof(FileItemIcon)
            .GetField("Extensions", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var assets = new List<Asset>();
        var rendered = new Dictionary<string, BitmapSource>();
        foreach (var item in icons)
        {
            var colors = FindColors(item.Value.Drawing).Distinct().ToArray();
            Emit(item.Key, item.Value, "src/Boardcast.Windows/FileItemIcon.cs", "source", colors);
        }
        var macFolderDrawing = icons["folder"].Drawing.Clone();
        Recolor(macFolderDrawing, Color.FromRgb(0x5A, 0xBD, 0xE6));
        Emit("folder-macos", new DrawingImage(macFolderDrawing), "src/Boardcast.Windows/FileItemIcon.cs", "preview-only-blue-fill", ["#5ABDE6"]);
        var section = Regex.Match(platformSource, @"public string PlatformIcon =>(?<body>[\s\S]*?)\n\s*};").Groups["body"].Value;
        var cases = Regex.Matches(section, "(?<key>[^\\r\\n]+)=>\\s*\"(?<path>[^\"\\r\\n]+)\"");
        if (cases.Count != 3) throw new InvalidOperationException("PlatformIcon source no longer has three supported cases; review the exporter.");
        foreach (Match match in cases)
        {
            string key = match.Groups["key"].Value;
            string id = key.Contains("windows") ? "platform-windows" : key.Contains("macos") ? "platform-macos" : "platform-unknown";
            var geometry = Geometry.Parse(match.Groups["path"].Value);
            var bounds = geometry.Bounds;
            double scale = Math.Min(24 / bounds.Width, 24 / bounds.Height);
            var frame = new DrawingGroup();
            frame.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 24, 24))));
            var shape = new DrawingGroup { Transform = new MatrixTransform(scale, 0, 0, scale,
                (24 - bounds.Width * scale) / 2 - bounds.X * scale,
                (24 - bounds.Height * scale) / 2 - bounds.Y * scale) };
            shape.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(0x62, 0x6D, 0x7D)), null, geometry));
            frame.Children.Add(shape);
            Emit(id, new DrawingImage(frame), "src/Boardcast.Windows/MainWindow.xaml.cs:PeerView.PlatformIcon", "source-neutral-muted", ["#626D7D"]);
        }
        if (icons.Count != 32 || assets.Count != 36) throw new InvalidOperationException($"Expected 32 source types and 36 total assets, got {icons.Count} / {assets.Count}.");
        var named = new Dictionary<string, string>
        {
            [".gitignore"]="git", [".gitattributes"]="git", [".gitmodules"]="git", [".gitkeep"]="git",
            ["dockerfile"]="settings", ["makefile"]="settings", ["cmakelists.txt"]="settings", [".editorconfig"]="settings", [".env"]="settings",
            ["readme"]="document", ["license"]="document", ["licence"]="document", ["copying"]="document"
        };
        var classificationSamples = new List<object>();
        foreach (var item in extensions.Concat(named))
        {
            string sample = item.Key.StartsWith('.') && !named.ContainsKey(item.Key) ? "sample" + item.Key : item.Key;
            if (!ReferenceEquals(FileItemIcon.Get(sample, false), icons[item.Value])) throw new InvalidOperationException("Classification mismatch: " + sample);
            classificationSamples.Add(new { sample, type = item.Value });
        }
        if (!ReferenceEquals(FileItemIcon.Get("unknown.bcx", false), icons["file"]) ||
            !ReferenceEquals(FileItemIcon.Get("folder", true), icons["folder"])) throw new InvalidOperationException("Fallback classification changed.");
        string licenseSource = Path.Combine(repo, "docs", "third-party", "Material-Icon-Theme.LICENSE");
        File.Copy(licenseSource, Path.Combine(output, "Material-Icon-Theme.LICENSE"), true);
        var catalog = new
        {
            schemaVersion = 1,
            previewOnly = true,
            sourceVersion = version,
            pixelSize = size,
            sourceTypeCount = icons.Count,
            assetCount = assets.Count,
            sourceHashes = new Dictionary<string, string>
            {
                ["src/Boardcast.Windows/FileItemIcon.cs"] = Hash(File.ReadAllBytes(Path.Combine(work, "FileItemIcon.cs"))),
                ["src/Boardcast.Windows/MainWindow.xaml.cs"] = Hash(File.ReadAllBytes(Path.Combine(work, "PlatformSource.txt")))
            },
            license = new { name = "MIT", upstream = "Material Icon Theme / Material Extensions", source = "docs/third-party/Material-Icon-Theme.LICENSE", included = "Material-Icon-Theme.LICENSE" },
            nativeFolderOverride = new
            {
                windows = "folder.png preserves source geometry and #E3AC45 fill",
                macos = "Prefer NSWorkspace system folder icon in the native app; folder-macos.png is only a Windows-authored preview representative.",
                previewBlue = "#5ABDE6",
                sameGeometry = true
            },
            render = new { format = "PNG", alpha = "transparent BGRA32", platformStretch = "Uniform, matching WPF Path Stretch", localFileAccess = "Classification uses names and extensions only; export opens no sample file." },
            extensionMapping = extensions,
            namedMapping = named,
            fallback = "file",
            directory = "folder",
            platformMapping = new Dictionary<string, string> { ["windows"] = "platform-windows", ["macos"] = "platform-macos", ["mac"] = "platform-macos", ["osx"] = "platform-macos", ["unknown"] = "platform-unknown" },
            assets,
            verification = new { classificationChecks = classificationSamples.Count + 2, all112IfDefault = size == 112, allTransparent = true, allNonEmpty = true, fileGeometry = "Compiled from an unchanged source snapshot.", platformGeometry = "Extracted verbatim from PlatformIcon switch.", sourceColorsPreserved = "All 32 source icon fills retained; only documented Mac folder variant recolored." }
        };
        File.WriteAllText(Path.Combine(output, "catalog.json"), JsonSerializer.Serialize(catalog, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        DrawContactSheet(Path.Combine(output, "contact-sheet.png"), assets, rendered);
        Console.WriteLine($"Exported {icons.Count} original file/folder types + 1 Mac folder variant + 3 platform icons ({assets.Count} PNGs), {size} x {size}, transparent alpha. {classificationSamples.Count + 2} classification checks passed.");
        Console.WriteLine(Path.Combine(output, "catalog.json"));

        void Emit(string id, DrawingImage image, string source, string variant, string[] colors)
        {
            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen()) context.DrawImage(image, new Rect(0, 0, size, size));
            var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            byte[] pixels = new byte[size * size * 4];
            bitmap.CopyPixels(pixels, size * 4, 0);
            int clear = 0, painted = 0;
            for (int i = 3; i < pixels.Length; i += 4) { if (pixels[i] == 0) clear++; else painted++; }
            if (clear == 0 || painted == 0) throw new InvalidOperationException("Invalid alpha: " + id);
            foreach (string color in colors)
            {
                var parsed = (Color)ColorConverter.ConvertFromString(color);
                bool found = false;
                for (int i = 0; i < pixels.Length; i += 4)
                    if (pixels[i] == parsed.B && pixels[i + 1] == parsed.G && pixels[i + 2] == parsed.R && pixels[i + 3] == 255) { found = true; break; }
                if (!found) throw new InvalidOperationException("Source color was not retained: " + id + " " + color);
            }
            string path = Path.Combine(output, id + ".png");
            SavePng(bitmap, path);
            var decoded = new BitmapImage();
            decoded.BeginInit(); decoded.CacheOption = BitmapCacheOption.OnLoad; decoded.UriSource = new Uri(path); decoded.EndInit();
            if (decoded.PixelWidth != size || decoded.PixelHeight != size) throw new InvalidOperationException("PNG dimensions changed: " + id);
            assets.Add(new Asset(id, id + ".png", source, variant, colors, size, size, clear, painted, Hash(File.ReadAllBytes(path))));
            rendered.Add(id, bitmap);
        }
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static IEnumerable<string> FindColors(Drawing drawing)
    {
        if (drawing is GeometryDrawing geometry && geometry.Brush is SolidColorBrush brush && brush.Color.A > 0) yield return $"#{brush.Color.R:X2}{brush.Color.G:X2}{brush.Color.B:X2}";
        if (drawing is DrawingGroup group) foreach (var child in group.Children) foreach (string color in FindColors(child)) yield return color;
    }
    private static void Recolor(Drawing drawing, Color color)
    {
        if (drawing is GeometryDrawing geometry && geometry.Brush is SolidColorBrush brush && brush.Color.A > 0) geometry.Brush = new SolidColorBrush(color);
        if (drawing is DrawingGroup group) foreach (var child in group.Children) Recolor(child, color);
    }
    private static void SavePng(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
    private static void DrawContactSheet(string path, IReadOnlyList<Asset> assets, IReadOnlyDictionary<string, BitmapSource> rendered)
    {
        const int cols = 6, cellW = 170, cellH = 112, header = 54;
        int rows = (assets.Count + cols - 1) / cols, width = cols * cellW, height = header + rows * cellH;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xF7, 0xF8, 0xFA)), null, new Rect(0, 0, width, height));
            Text("Boardcast icon assets — source geometry, macOS folder color variant", 18, 18, 14, Color.FromRgb(0x20, 0x24, 0x2B));
            for (int i = 0; i < assets.Count; i++)
            {
                int x = (i % cols) * cellW, y = header + (i / cols) * cellH;
                dc.DrawRoundedRectangle(Brushes.White, new Pen(new SolidColorBrush(Color.FromRgb(0xE2, 0xE6, 0xEC)), 1), new Rect(x + 8, y + 4, cellW - 16, cellH - 12), 7, 7);
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0x24, 0x28, 0x30)), null, new Rect(x + 90, y + 13, 52, 52), 5, 5);
                dc.DrawImage(rendered[assets[i].Id], new Rect(x + 26, y + 19, 40, 40));
                dc.DrawImage(rendered[assets[i].Id], new Rect(x + 96, y + 19, 40, 40));
                Text(assets[i].Id, x + 18, y + 76, 11, Color.FromRgb(0x62, 0x6D, 0x7D));
            }
            void Text(string text, double x, double y, double size, Color color) => dc.DrawText(new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, new SolidColorBrush(color), 1), new Point(x, y));
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual); SavePng(bitmap, path);
    }
}
