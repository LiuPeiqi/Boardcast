using System.Windows;
using System.Windows.Controls;
using Boardcast.Core;

namespace Boardcast.Windows;

public sealed class SubnetShareDialog : EditorDialog
{
    private readonly List<(DiscoverySubnet Subnet, CheckBox Box)> _subnets = [];
    private readonly List<(PeerInfo Peer, CheckBox Box)> _peers = [];
    private readonly TextBlock _validation = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };

    public List<DiscoverySubnet> SelectedSubnets => _subnets.Where(choice => choice.Box.IsChecked == true).Select(choice => choice.Subnet).ToList();
    public List<PeerInfo> SelectedPeers => _peers.Where(choice => choice.Box.IsEnabled && choice.Box.IsChecked == true).Select(choice => choice.Peer).ToList();

    public SubnetShareDialog(Window owner, List<DiscoverySubnet> subnets, List<(PeerInfo Peer, string Name)> peers, HashSet<string> selectedPeers)
        : base(owner, "分享可发现的网段", "发起分享")
    {
        AddNote("选择要分享的网段和接收设备。对方可以接受或拒绝；接受后会记住这些网段并自动查找设备。");
        _fields.Children.Add(_validation);
        AddNote("网段（可多选，最多 32 个）");
        foreach (var subnet in subnets)
        {
            var box = AddCheck($"{subnet.Cidr} · 端口 {subnet.Port}", _subnets.Count < 32);
            box.Tag = subnet;
            _subnets.Add((subnet, box));
        }
        AddNote("接收设备（可多选，最多 32 台）");
        foreach (var (peer, name) in peers)
        {
            var box = AddCheck($"{name}{(peer.ReceivingEnabled ? "" : "（已暂停接收）")}\n{peer.Address} · {peer.Platform}",
                peer.ReceivingEnabled && selectedPeers.Contains(peer.Fingerprint));
            box.IsEnabled = peer.ReceivingEnabled;
            box.Tag = peer;
            _peers.Add((peer, box));
        }
    }

    protected override bool TryAccept()
    {
        _validation.Text = SelectedSubnets.Count == 0 ? "请至少选择一个网段。" : SelectedSubnets.Count > 32 ? "一次最多分享 32 个网段。" : SelectedPeers.Count switch
        {
            0 => "请至少选择一台接收设备。",
            > 32 => "一次最多向 32 台设备分享网段。",
            _ => ""
        };
        return _validation.Text.Length == 0;
    }
}
