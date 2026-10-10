# Boardcast LAN protocol v1

This specification describes the Windows 0.2.4 transport in `Boardcast.Core`: protocol version 1 with an optional `chunks-v1` capability. Peers without the capability retain the legacy stream format. The native Swift implementation includes the optional group metadata, but has not been built on macOS as part of this iteration; cross-platform interoperability still requires Mac builds and physical-device testing.

## Network boundary and discovery

- No account server, relay, STUN, TURN, NAT traversal, internet lookup or WAN endpoint.
- The TCP listener accepts loopback, RFC1918 IPv4, IPv4 link-local, IPv6 ULA and IPv6 link-local. IPv4-mapped IPv6 is normalized. Outgoing manual connections require a literal allowed IP, so hostnames cannot cause external DNS queries.
- UDP discovery uses IPv4 port **45831**, TTL **1**, local subnet broadcasts, startup/reconnect announce plus probe, active network-availability probes, and one announcement every **60 seconds** while LAN-online. An explicit Discover action emits a probe. Saved private IPv4 subnets also receive bounded unicast probes as described below.
- TCP uses the advertised port; default **45831**. Listening port changes require daemon restart with `--port`.
- Discovery packets are unauthenticated hints. Trust and subsequent file sending are pinned to the TLS certificate fingerprint, never to a name, IP address or discovery claim. First-time manual IP probing obtains the identity but does not mark it trusted.
- Announcement/probe datagrams are UTF-8 JSON, maximum 2048 bytes:

```json
{"protocolVersion":1,"type":"announce","fingerprint":"64 lowercase hex characters","name":"Office PC","platform":"windows","port":45831,"receivingEnabled":true}
```

`type` is `probe`, `announce` or `goodbye`. Optional fields include `groupName`, `lanOnline`, `appVersion`, `runtime`, `updatePort`, and `updateVersion`. An endpoint-matched goodbye sets an unauthenticated `departureHint`; it never deletes a certificate identity or refreshes its lifetime. The received datagram source supplies the address. A probe receives a rate-limited announcement. Peers expire after 150 seconds.

`groupName` is an optional string in discovery and hello `peer`. Missing or empty values select name-prefix inference. Explicit names are trimmed, contain no control characters and are limited to 80 UTF-16 code units. Inferred groups split the original device name on `-`, `_`, `.`, or whitespace and take the first nonempty segment (or `未分组` if empty); comparisons ignore case. Per-user known-device overrides and `peerGroupFilter` / `visibleGroups` preferences stay local and are never broadcast. Group metadata does not establish trust or grant receiving/relay privileges. Filtering does not remove peers from the daemon snapshot or update catalog. Legacy protocol v1 readers ignore the optional field.

## Remembered subnet discovery and sharing

A successful explicit IP connection may persist the private IPv4 subnet containing the authenticated socket endpoint. The optional hello `peer.discoverySubnets` array contains interface hints as `{ "cidr": "192.168.20.0/24", "port": 45831 }`. Only a CIDR containing the connected IP is learned; older peers without this field use the connected IP's `/24`. UDP hints and ordinary file connections do not automatically persist subnets or create trust.

Only an explicit authenticated IP connection or explicit local acceptance authorizes remembered-subnet probing. Ordinary UDP discovery, file hellos, and unapproved subnet offers cannot expand that scope. Sharing selects from current local interfaces or already remembered ranges; there is no NAT mapping or traversal. Saved entries contain a canonical RFC1918 IPv4 CIDR with prefix `/16` through `/32` and a TCP port from 1024 through 65535. At most 32 entries are accepted; public, loopback, link-local and IPv6 ranges are rejected. An interface wider than `/16` falls back to the seed `/24`. `/31` and `/32` include all addresses; other prefixes omit network/broadcast addresses. The daemon publishes local interface subnets separately from remembered entries so users may share either.

Remembered subnets are probed by rate-limited UDP unicast on discovery port 45831, with TTL 64; local broadcasts retain TTL 1. A probe receives an announcement directed back to its source, as well as any legacy local broadcast behavior. TCP hello probing supports devices whose UDP discovery is unavailable. Each complete sweep waits 60 seconds before the next. Forget/offline/shutdown cancel the relevant work and suppress late observations. Discovered identities retain normal 150-second expiry and never gain trust from subnet membership.

Subnet sharing is an optional `subnet-sharing-v1` hello capability on the existing mTLS file port. The sender pins the selected fingerprint, completes hello, and requires the remote capability before sending:

```json
{"type":"subnet-offer","version":1,"transferId":"32 lowercase GUID hex characters","subnets":[{"cidr":"192.168.20.0/24","port":45831}]}
```

The receiver validates the authenticated sender, ID, range/port limits and online/receiving state, then responds `subnet-pending` with the same `transferId` while requesting explicit local approval. File trust/rules never automatically accept subnet data. Approval expires after 120 seconds. After successful persistence, the receiver responds `subnet-accept`; rejection or inability to save responds `subnet-reject`, with the same `transferId` and an optional `message`. No file payload or destination directory follows. Each recipient has an independent request/result, and no confirmation causes propagation to further peers.

Current-user IPC adds `subnet-share` (`peers`, `subnets`), `subnet-forget` (`subnets`), and `subnet-decide` (`transferId`, `accept`). Snapshots include `localDiscoverySubnets`, settings `discoverySubnets`, and separate `pendingSubnets` approval items. Transfer records use `kind: "subnets"`. Omitting new fields preserves legacy decoding; unsupported peers receive no subnet offer.

## Persistent identity and TLS

Each installation stores a P-256 self-signed certificate and private key in `identity.pfx` with private current-user permissions. Its identity is SHA-256 over the complete DER certificate, represented as **64 lowercase hexadecimal characters**. Windows imports the key using UserKeySet because Schannel cannot use ephemeral imported keys.

All TCP application traffic uses TLS 1.2 or TLS 1.3 with client certificates required. Both sides prove possession of their private keys during TLS. The sender verifies the receiver certificate against the selected fingerprint. The receiver derives the sender fingerprint from the presented client certificate and checks both hello and offer against it. The self-signed certificate is the device identity; public Web PKI/hostname validation is not used.

Discovery itself cannot establish trust. Users should compare full fingerprints out of band before enabling automatic receipt. Rotating/deleting an identity produces a different device and does not inherit trust.

## Framing and messages

A JSON frame is a **4-byte unsigned big-endian length**, followed by exactly that many UTF-8 JSON bytes. Valid frame payload length: 2 bytes to **4 MiB**. Property names use camelCase. Unknown JSON properties may be ignored. The exact file payload format below is raw binary and has no JSON wrapping.

Immediately after TLS, client sends:

```json
{"type":"hello","version":1,"peer":{"fingerprint":"...","name":"Office PC","platform":"windows","address":"","port":45831,"lastSeen":"2026-10-08T00:00:00Z","receivingEnabled":true}}
```

Server validates the identity/version and returns the same hello structure containing its own peer. New Windows peers include `"capabilities":["chunks-v1"]` in hello; the offer repeats this capability only after the remote hello advertises it. Unknown properties are ignored by legacy peers. Addresses in hello are not authoritative; the connected socket endpoint is used. A discovery-only connection then sends `{"type":"bye"}` and closes.

For sending, the client instead sends:

```json
{"type":"offer","version":1,"offer":{"id":"32 lowercase GUID hex characters","senderName":"Office PC","senderFingerprint":"...","summary":"Sender-provided optional description","entries":[{"relativePath":"Project","size":0,"isDirectory":true},{"relativePath":"Project/readme.txt","size":123,"isDirectory":false}],"totalBytes":123}}
```

The authoritative total is the checked sum of entry sizes, not the supplied computed `totalBytes`. Summary text is untrusted sender-provided content, not proof of a file's purpose or safety. Absolute source paths are never part of this message.

The server validates identity, manifest, policy and receiving-enabled state before creating receive content. It sends `{"type":"pending"}` while an approval is resolved, then either `{"type":"accept"}` or `{"type":"reject","message":"Reason"}`. Automatic approval requires exactly one known fingerprint, trusted=true, device autoAccept=true, and the first matching enabled rule with autoAccept=true. Rules match the entire batch. Unknown peers require manual approval. Receiving-paused rejects immediately.

## Legacy stream payload

When `chunks-v1` is not negotiated, after acceptance, for **each non-directory entry in manifest order**, the sender writes:

1. Exactly `entry.size` raw bytes.
2. Exactly **32 raw bytes** containing SHA-256 of that file's payload.

Directory entries produce no bytes. An empty file therefore contributes its SHA-256 digest only. There are no file-by-file acknowledgements or extra chunk headers. SHA-256 is calculated while streaming, so neither endpoint pre-reads a whole file to hash it. Senders use bounded 256 KiB pooled buffers. Files are not precompressed or archived, avoiding extra disk I/O, decompression hazards and archive expansion.

After every entry has been verified and the entire batch committed, server sends `{"type":"complete","message":"Received and verified."}`. A receiver-side error sends `reject` if possible, then closes the connection. Cancellation closes the transfer connection. A sender shows completed only after complete; a receiver whose final acknowledgement is lost retains its committed completed state. In the latter case the sender cannot distinguish lost acknowledgement from an incomplete commit; users should check the receiver before retrying. This legacy payload has no block resume. Resuming a local task to such a peer sends the payload again; users should check the receiver if the legacy final acknowledgement was lost.

## Negotiated chunk payload and recovery

When both hello and offer advertise `chunks-v1`, the offer includes a random `batchId`, shared across recipients of that send, while `offer.id` remains unique per recipient. After the same acceptance and path-policy checks, the sender transmits `chunk-start` with a `chunks` manifest, a recipient grant, and an initial authenticated list of potential sources.

The chunk manifest contains `id`, `chunkSize`, and `files`: each file has `relativePath`, `size`, `isDirectory`, and SHA-256 `hashes` in chunk order. The content ID binds the canonical manifest. The manifest must match the accepted offer exactly. Hashes are prepared before sending; source bytes are rechecked against the manifest when serving them. Default chunk size is 1 MiB and increases up to 8 MiB to keep the manifest within the 3.5 MiB budget; the general 4 MiB frame limit still applies. Empty files and directory entries have no payload chunks.

A receiver requests `{"type":"chunk","chunk":{"fileIndex":0,"chunkIndex":0},"verifiedBytes":0}`. The source responds with a JSON `chunk` frame naming the requested chunk and `chunkBytes`, immediately followed by exactly that many raw bytes. The reply may carry refreshed candidate sources, active recipient count and verified-byte hints. Every chunk is checked against the manifest before it becomes reusable or shareable. Source availability is a scheduling hint, never an integrity check or authorization.

`ChunkTransfer` schedules a bounded number of missing blocks; `ChunkStore` reads and writes them at their exact file offsets. On reopening a cache, it rehashes existing content instead of trusting a saved bitset. Files are completed only after a final full chunk verification. No unverified chunk can be read by another recipient. The same engine is used for update packages, whose additional release-signature verification is described below.

Partial data is stored in a private task directory under the receiving device's application data directory. Its key binds sender fingerprint, transfer ID, content ID and selected destination, so unrelated senders, changed manifests and different destinations cannot inherit recovery authorization. Inactive task data older than seven days and excess completion receipts are eligible for bounded cleanup at startup and subsequent receipt. This cache is not a global content index or deduplication service.

Each verified file is committed directly into the approved receive root, preserving its relative directory structure. Existing names receive numeric suffixes instead of being overwritten. Private records in the application's data directory bind sender fingerprint, transfer ID, content ID, selected destination and committed file mappings; retries revalidate the committed files and can reuse them after interruption or a lost final acknowledgement, including after restart. No completion receipt JSON or task wrapper directory is left alongside the received files. During cache verification and transfer, `chunk-keepalive` / `pending` frames keep the accepted origin session alive without moving payload. The receiver then sends `chunks-complete` with `reusedBytes` and `peerBytes`; the sender replies `complete`. A committed receive remains completed even if this final exchange is interrupted.

The daemon separately persists local outgoing tasks in `recoverable-sends.json`, including source paths/exclusions, description, batch ID, transfer ID and pinned recipient. The journal is bounded to 128 tasks and 4 MiB; stale records older than seven days are ignored at startup. Restart restores them as paused, never automatically starts them. `resume` rebuilds the source manifest, refreshes the same fingerprint's discovered endpoint, and repeats normal receiving approval. Explicit `cancel`, rejection or completion removes the local resume entry. A sender's explicit cancellation also sends a `transfer-cancel` control message over a separate pinned mTLS connection when the recipient advertises `receive-cancel-v1`. The recipient authenticates the original sender before stopping and cleaning that task; another identity cannot cancel it by naming its ID. Pause, network failure and process shutdown do not send this control message. If the recipient is unreachable or runs an older implementation, immediate remote cleanup cannot be confirmed; its recovery expiry rules still apply.

## Authorized peer sources

Ordinary file sharing is restricted to recipients accepted into the same batch. The original sender signs a grant with its device ECDSA key covering a unique `grantId`, `batchId`, `contentId`, `senderFingerprint`, `recipientFingerprint`, and an expiry timestamp. Grants have a two-hour maximum lifetime; retries receive a distinct grant so an old revocation cannot revoke the new attempt. A relay already has the authenticated sender certificate from its original receive; it verifies both the signature and the requesting mTLS client's fingerprint before serving a block.

A relay connection exchanges the normal hello, then receives `chunk-share` with `batchId`, `contentId` and `grant`. Before returning `share-ready`, it opens an authenticated `chunk-authorize` request to the original sender and requires `authorized`; unavailable or withdrawn authorization fails closed. An unfinished recipient connection removes that recipient from the approved set and triggers authenticated `chunk-revoke` notifications to remaining sources. Relays remember revocations for the specific signed grant and check them on each request. An established session rechecks authorization with the sender after a 30-second lease expires, before accepting another block request. Revocation push delivery is best-effort: if it fails, an existing session can continue within the remaining lease, and an already-started block may finish. Cancellation is not an instantaneous remote recall and cannot retract bytes already received. At most two relay sessions are served per device. Each subsequent `chunk` request repeats that authorization. A missing or not-yet-verified block returns `chunk-missing`; a successful response uses the same JSON-plus-raw-byte chunk framing. The requester cannot name an arbitrary local path. The source rechecks stored bytes when reading; both source and requester reject changed content.

Peer transfer is conditional. Ordinary file payload must be at least 64 MiB, the origin must have at least two active recipients, the candidate must report at least two chunks verified, and the receiver must first measure two origin chunks. Peer probes also require at least 8 MiB remaining and a requested chunk of at least 256 KiB. There is at most one unknown-source probe globally and one in-flight request per peer; ordinary receives use up to three chunk workers and keep origin requests in the schedule. A peer needs measured throughput at least 80% of the origin rate to remain useful. Slow or failed sources back off for 30 seconds up to five minutes; probes use a bounded 2–10 second timeout and failures return to the origin. These are local tuning values, not compatibility requirements or proven LAN speed gains. See [distribution design](multi-recipient-transfer.md) and [primary-source research](p2p-distribution-research.md).

## Update packages

Updates retain a separate mTLS listener and signed-release manifest handshake because executable installation has a stronger trust boundary than ordinary receipt. The package itself is represented as one file, `Boardcast-update.zip`, and uses the same `ChunkTransfer`, `ChunkStore`, chunk requests, SHA-256 checks, bounded scheduling and recovery. The downloader verifies the selected device, fixed local release trust root, compatible newer version and signed package metadata before using content. It verifies the signed full ZIP digest and extracted file inventory before installation.

Discovery lists available newer compatible versions but never initiates a download. The user selects a version and explicitly starts its transfer. Old `autoDownloadUpdates` settings are normalized to false. Downloads preserve verified chunks on pause or interruption; startup revalidates the selected-package record and cache, restores paused/ready state, and does not fetch or install automatically. Update sources need the same signed package hash, size and chunk content ID. The selected source starts immediately; up to two additional candidates are connected lazily. At least 64 MiB of missing content and a measured primary rate below 32 MiB/s are required for acceleration probes; the tail/chunk-size guards also apply. A primary disconnect may use a verified alternate even for a small package, as failover. Unlike ordinary batches, these sources supply already published signed release packages. No WAN source is used. Details and installation rollback boundaries are in [peer updates](peer-updates.md).

## Filesystem and approval safety

- Default maximum: 100 GiB per transfer and 50,000 entries; both are configurable within validated bounds. Metadata has a separate 3 MiB estimated JSON budget.
- No traversal, absolute paths, ADS, Windows reserved device names, duplicate/case-folded/Unicode-normalized name collisions, nonzero directory sizes, negative/overflowing sizes, or a file being an ancestor of another entry.
- Source symlinks/reparse points and receive paths through symlinks/reparse points are rejected. Sender rechecks source size and disallows concurrent writers on Windows.
- After approval, receive staging and recovery records live in the local application's private data directory. Chunk receipt revalidates that task's cache and committed file mapping. Private staging directories grant only the current user full control on Windows; Unix helpers use mode 0700.
- Legacy files are opened CreateNew; chunk cache files use validated paths and manifest checks. Each file passes its streamed or chunk SHA-256 checks before commit into the chosen directory. Commit never overwrites existing content, uses stable conflict mappings, and records the final path before reporting that file saved. Completed files can supply peer chunks without retaining duplicate partial files.
- Explicit receiver cancellation persists a cancellation marker, stops workers and closes handles, then removes only task-owned incomplete data and empty directories. Already committed files survive cancellation and failures. Deletion failures remain eligible for retry at startup or subsequent receipt. Interrupted chunk tasks preserve valid progress for recovery; cancelling a local send cannot erase a recipient's committed files. Destination changes and receiving-enabled/manifest limits are revalidated after pending manual approval.
- A hostile process already running as the same OS user is outside the identity and staging isolation boundary.

## Resource limits and local control

The listener allows eight concurrent connections. Handshake/initial exchange times out after 15 seconds, approval after five minutes, and stalled payload I/O after 90 seconds. At most four outgoing TLS transfers run concurrently, with up to 128 queued recipient tickets (1–32 targets per multi-send request) and four pending requests admitted. Pending JSON metadata is limited to 2 MiB in total to preserve a usable IPC snapshot. Progress is normally published at 200 ms intervals; history is bounded.

The local daemon exposes current-user-only named-pipe IPC; pipe names derive from user identity and the normalized data directory. IPC uses the same framed JSON codec. One request yields one response and the connection closes. Commands: status, discover, connect, settings, set-online, send, send-many, decide, pause, resume, cancel, update-download, update-cancel, update-install, stop. `send` returns a transfer ID promptly while manifest enumeration and transfer continue in the background. `--data-dir PATH --port N --no-ui` support isolated instances and test setups. `--service` uses Windows SCM and never opens desktop UI.

A local `send` request accepts either `paths` (whole items) or `sources` (`path` plus `excludedPaths` relative to each source folder). Explicit child paths are excluded before enumeration descends into them. Folder-size scans and outgoing manifests use the same filter; only included entries and bytes appear in the network offer. No wildcard matching is implied.

No remote protocol message executes a process, CLI, skill or MCP tool. LLM/CLI integrations are local, opt-in preprocessing and operate independently of remote receive messages.

## Validation

`dotnet run --project tests/Boardcast.TransportTests -c Release` exercises the live TLS transport over loopback, persistent identities, recipient pinning, paused receiver, changing source size, selective folder exclusions, Unicode/empty files and folders, traversal, spoofed offer identities, bad streamed hashes and staging cleanup. `scripts/Measure-Local.ps1` measures independent daemon/CLI processes and idle resource use. Loopback numbers are not claims about real NIC, Wi-Fi, storage or cross-platform throughput.


LAN-online is independent of receiving-enabled. Offline rejects new discovery, direct-IP connections and sends. Pending/queued sends are cancelled; accepted payloads may complete. Reconnect announces and probes immediately. Multi-send enumerates once and allocates independent transfer IDs per recipient. See [multi-recipient transfer design](multi-recipient-transfer.md) for recovery, conditional peer scheduling and deferred multicast work. macOS, QUIC, Wake-on-LAN, global content deduplication and platform-specific file metadata preservation are outside this iteration.
