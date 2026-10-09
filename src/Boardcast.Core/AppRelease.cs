using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Boardcast.Core;

public static class AppRelease
{
    public static string CurrentVersion => typeof(AppRelease).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.2.3";
    public static string RuntimeIdentifier => (OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux") + "-" + RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
}

/// <summary>SemVer 2.0 precedence; build metadata never changes precedence.</summary>
public sealed partial class ReleaseVersion : IComparable<ReleaseVersion>
{
    private readonly ulong major, minor, patch;
    private readonly string[] prerelease;
    public string Text { get; }
    public bool IsPrerelease => prerelease.Length != 0;
    private ReleaseVersion(string text, ulong major, ulong minor, ulong patch, string[] prerelease)
    { Text = text; this.major = major; this.minor = minor; this.patch = patch; this.prerelease = prerelease; }
    [GeneratedRegex(@"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?\z", RegexOptions.CultureInvariant)]
    private static partial Regex Format();
    public static bool TryParse(string? text, out ReleaseVersion? version)
    {
        version = null;
        if (text is null || text.Length > 128) return false;
        var match = Format().Match(text);
        if (!match.Success || !ulong.TryParse(match.Groups[1].Value, out var a) || !ulong.TryParse(match.Groups[2].Value, out var b) || !ulong.TryParse(match.Groups[3].Value, out var c)) return false;
        var pre = match.Groups[4].Success ? match.Groups[4].Value.Split('.') : [];
        if (pre.Any(part => part.All(char.IsAsciiDigit) && part.Length > 1 && part[0] == '0')) return false;
        version = new(text, a, b, c, pre); return true;
    }
    public static ReleaseVersion Parse(string text) => TryParse(text, out var version) ? version! : throw new InvalidDataException("发行版本必须符合语义版本格式，例如 1.2.0。");
    public int CompareTo(ReleaseVersion? other)
    {
        if (other is null) return 1;
        int n = major.CompareTo(other.major); if (n != 0) return n;
        n = minor.CompareTo(other.minor); if (n != 0) return n;
        n = patch.CompareTo(other.patch); if (n != 0) return n;
        if (prerelease.Length == 0) return other.prerelease.Length == 0 ? 0 : 1;
        if (other.prerelease.Length == 0) return -1;
        for (int i = 0; i < Math.Min(prerelease.Length, other.prerelease.Length); i++)
        {
            string x = prerelease[i], y = other.prerelease[i];
            bool nx = x.All(char.IsAsciiDigit), ny = y.All(char.IsAsciiDigit);
            n = nx && ny ? x.Length.CompareTo(y.Length) : nx != ny ? (nx ? -1 : 1) : 0;
            if (n == 0) n = string.Compare(x, y, StringComparison.Ordinal);
            if (n != 0) return n;
        }
        return prerelease.Length.CompareTo(other.prerelease.Length);
    }
}

public sealed record PeerUpdateVersion(string Version, IReadOnlyList<PeerInfo> Sources);

public static class PeerUpdateCatalog
{
    public static IReadOnlyList<PeerUpdateVersion> AvailableVersions(IEnumerable<PeerInfo> peers, string currentVersion, string runtime) =>
        peers.Where(peer => peer.LanOnline && !peer.DepartureHint && IsNewerCompatible(peer, currentVersion, runtime))
            .GroupBy(peer => peer.UpdateVersion, StringComparer.Ordinal)
            .OrderByDescending(group => ReleaseVersion.Parse(group.Key))
            .Select(group => new PeerUpdateVersion(group.Key, group.OrderBy(peer => peer.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(peer => peer.Fingerprint, StringComparer.Ordinal).Select(peer => peer with { }).ToList()))
            .ToList();
    public static bool IsNewerCompatible(PeerInfo peer, string currentVersion, string runtime)
    {
        if (peer.UpdatePort is < 1 or > 65535 || peer.Runtime != runtime || !ReleaseVersion.TryParse(currentVersion, out var current) ||
            !ReleaseVersion.TryParse(peer.UpdateVersion, out var candidate)) return false;
        return candidate!.CompareTo(current) > 0 && (current!.IsPrerelease || !candidate.IsPrerelease);
    }
    public static PeerInfo? Recommend(IEnumerable<PeerInfo> peers, string currentVersion, string runtime, string? pinnedFingerprint = null)
    {
        var available = peers.Where(p => p.LanOnline && !p.DepartureHint && IsNewerCompatible(p, currentVersion, runtime)).ToList();
        var pinned = available.FirstOrDefault(p => p.Fingerprint.Equals(pinnedFingerprint, StringComparison.OrdinalIgnoreCase));
        return pinned ?? available.OrderByDescending(p => ReleaseVersion.Parse(p.UpdateVersion)).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Fingerprint, StringComparer.Ordinal).FirstOrDefault();
    }
}
