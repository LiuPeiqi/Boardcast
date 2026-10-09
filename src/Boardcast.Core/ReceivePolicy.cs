using System.Text.RegularExpressions;

namespace Boardcast.Core;

public static class ReceivePolicy
{
    public static ReceiveDecision Evaluate(AppSettings settings, TransferOffer offer, string authenticatedFingerprint)
    {
        PathSafety.ValidateManifest(offer, settings);
        if (!IsFingerprint(authenticatedFingerprint) || !string.Equals(offer.SenderFingerprint, authenticatedFingerprint, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("发送方身份与 TLS 证书不一致。");

        var destination = PathSafety.ValidateReceiveRoot(settings.ReceiveDirectory);
        if (!settings.ReceivingEnabled) return new(false, destination, "已暂停接收");
        var known = settings.Devices.Where(d => string.Equals(d.Fingerprint, authenticatedFingerprint, StringComparison.OrdinalIgnoreCase)).ToList();
        var rule = settings.Rules.FirstOrDefault(r => r.Enabled && Matches(r, offer, authenticatedFingerprint));
        if (rule is not null && !string.IsNullOrWhiteSpace(rule.Destination)) destination = PathSafety.ValidateReceiveRoot(rule.Destination);

        if (known.Count != 1 || !known[0].Trusted)
            return new(false, destination, "未信任的设备，需要确认", rule?.Id);
        if (!known[0].AutoAccept) return new(false, destination, "此设备未启用自动接收", rule?.Id);
        if (rule is null) return new(false, destination, "未匹配自动接收规则");
        if (!rule.AutoAccept) return new(false, destination, "匹配的规则要求手动确认", rule.Id);
        return new(true, destination, $"按规则「{rule.Name}」自动接收", rule.Id);
    }

    private static bool Matches(ReceiveRule rule, TransferOffer offer, string fingerprint)
    {
        if (!string.IsNullOrWhiteSpace(rule.DeviceFingerprint) && !string.Equals(rule.DeviceFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase)) return false;
        if (rule.MaxBytes < 0 || (rule.MaxBytes > 0 && offer.TotalBytes > rule.MaxBytes)) return false;
        var roots = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in offer.Entries)
        {
            var path = PathSafety.NormalizeRelativePath(entry.RelativePath);
            var slash = path.IndexOf('/');
            var root = slash < 0 ? path : path[..slash];
            var directory = entry.IsDirectory || slash >= 0;
            roots[root] = roots.GetValueOrDefault(root) || directory;
        }
        if (rule.Scope == EntryScope.Files && roots.Values.Any(isDirectory => isDirectory)) return false;
        if (rule.Scope == EntryScope.Folders && roots.Values.Any(isDirectory => !isDirectory)) return false;
        if (!Enum.IsDefined(rule.Scope)) return false;
        if (!string.IsNullOrWhiteSpace(rule.FolderNamePattern))
        {
            if (rule.FolderNamePattern.Length > 256 || roots.Values.Any(isDirectory => !isDirectory)) return false;
            var pattern = "\\A" + Regex.Escape(rule.FolderNamePattern).Replace("\\*", ".*").Replace("\\?", ".") + "\\z";
            try
            {
                if (roots.Keys.Any(root => !Regex.IsMatch(root, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(50)))) return false;
            }
            catch (RegexMatchTimeoutException) { return false; }
        }
        if (rule.Extensions.Count > 0)
        {
            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var extension in rule.Extensions)
            {
                if (string.IsNullOrWhiteSpace(extension)) return false;
                var normalized = extension.Trim();
                if (!normalized.StartsWith('.')) normalized = "." + normalized;
                if (normalized.Length < 2 || normalized[1..].Any(c => c is '/' or '\\' or ':' or '*' or '?' || char.IsWhiteSpace(c))) return false;
                allowed.Add(normalized);
            }
            if (offer.Entries.Where(e => !e.IsDirectory).Any(e => !allowed.Contains(Path.GetExtension(e.RelativePath)))) return false;
        }
        return true;
    }

    private static bool IsFingerprint(string value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
}
