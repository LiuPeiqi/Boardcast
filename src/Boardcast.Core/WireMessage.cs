namespace Boardcast.Core;
public sealed record WireMessage
{
    public string Type { get; set; } = "";
    public int Version { get; set; } = 1;
    public PeerInfo? Peer { get; set; }
    public TransferOffer? Offer { get; set; }
    public string Message { get; set; } = "";
    public string TransferId { get; set; } = "";
    public List<string> Capabilities { get; set; } = [];
    public ChunkManifest? Chunks { get; set; }
    public ChunkRequest? Chunk { get; set; }
    public int ActiveRecipients { get; set; }
    public long VerifiedBytes { get; set; }
    public Dictionary<string, long> SourceVerifiedBytes { get; set; } = [];
    public int ChunkBytes { get; set; }
    public long ReusedBytes { get; set; }
    public long PeerBytes { get; set; }
    public string ContentId { get; set; } = "";
    public string BatchId { get; set; } = "";
    public ChunkGrant? Grant { get; set; }
    public List<PeerInfo> Sources { get; set; } = [];
}
