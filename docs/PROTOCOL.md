# Beam protocol, version 1

Everything a compatible implementation (e.g. a future phone app) needs.

## Discovery (UDP 47820)

UTF-8 JSON datagrams (≤ 2 KB) sent to multicast `239.255.73.37:47820`, to each adapter's subnet broadcast address,
and unicast to manually added peers.

```json
{"magic":"beam-discovery","v":1,"type":"announce","id":"6f0c…","name":"Diaz's PC","port":47821,
 "fp":"4f2a91c3…(sha-256 hex of TLS certificate)","platform":"windows","kind":"desktop","ver":"1.0.0"}
```

| type | meaning | response |
|---|---|---|
| `announce` | I exist (sent every 5 s while discoverable) | unicast `announce` to newcomers |
| `query` | Who is there? (carries the sender's own info unless `"hidden":true`) | unicast `announce` |
| `bye` | I'm leaving / hiding | remove the device |

A device is considered gone 16 s after its last packet. Ignore packets carrying your own `id`.

## Transfer connection (TCP 47821, or the announced port)

1. TCP connect; TLS 1.2+ handshake. **Both sides send a certificate** (self-signed is fine). The client compares
   the server certificate's SHA-256 with the fingerprint from discovery and aborts on mismatch.
2. All further data is a sequence of frames: `type (1 byte) | length (4 bytes, big-endian) | payload`.
   Control payloads are UTF-8 JSON (camelCase); `FileData` payloads are raw bytes (≤ 1 MB, normally 256 KB).
   Control frames are limited to 32 MB.

| # | Frame | Direction | Payload |
|---|---|---|---|
| 1 | `Hello` | C→S | `{protocol, minProtocol, deviceId, deviceName, platform, kind, appVersion, purpose:"transfer"\|"probe", port}` |
| 2 | `HelloReply` | S→C | same shape. Incompatible if `peer.protocol < my.minProtocol` or `peer.minProtocol > my.protocol`. A `probe` connection ends here. |
| 3 | `Offer` | C→S | `{transferId (≤64 alnum), resume, entries:[{p:"Photos/2026/a.jpg", s:size, d:isDir, m:unixMs}]}` |
| 4 | `OfferResponse` | S→C | `{accepted, reason?, plan:[{i:index, o:offset, done}]}` — files to send, starting byte, already-complete flag |
| 5 | `FileHeader` | C→S | `{index, offset}` then `FileData` frames from `offset` |
| 6 | `FileData` | C→S | raw bytes |
| 7 | `FileFooter` | C→S | `{index, length, sha256}` — length and hash of the **whole** file |
| 8 | `FileResult` | S→C | `{index, ok, retry, error?}` — `retry:true` asks the sender to re-send that file from byte 0 |
| 9 | `SourceFileError` | C→S | `{index, error}` — sender couldn't read the file (may interrupt a file mid-way) |
| 10 | `Done` | C→S | `{}` after all `FileResult`s were received |
| 11 | `Result` | S→C | `{completed, failed, skipped}` |
| 12 | `Cancel` | either | `{reason, detail?}` then close |

Paths always use `/`. Directory entries create (empty) folders; files in nested paths imply their parents.

`reason` values: `declined`, `timeout`, `busy`, `no_space`, `destination_unavailable`, `permission_denied`,
`write_failed`, `cancelled`, `shutdown`, `protocol`.

### Rules

- The receiver must never write outside its chosen folder, never write more bytes for a file than its declared
  size, and must verify `length` and `sha256` before exposing the file under its final name.
- The receiver may wait for its user before sending `OfferResponse` (up to ~2 minutes); meanwhile the sender may
  send `Cancel` or close the connection to withdraw.
- Resume: the sender reconnects and repeats the same `Offer` with the same `transferId` and `resume:true`. A receiver
  that has state for that transfer from the **same certificate** and identical entry list replies with offsets
  instead of asking its user again. Otherwise it treats the offer as new.
- Either side should treat 60 s without progress as a lost connection. TCP keep-alive is enabled.

## Text and links (protocol 2)

A connection whose `Hello.purpose` is `"text"` carries a single piece of text instead of files:

```
sender → Hello {purpose:"text", protocol:2, …}      receiver → HelloReply
sender → Text  {transferId, text}                    (frame type 13, UTF-8 JSON, up to 256 K characters)
receiver → Result {completed:1}                      the text was shown (or copied, for a trusted sender)
        or Cancel {reason:"declined"}                dismissed or timed out
```

- The sender checks `HelloReply.protocol ≥ 2` before sending the `Text` frame and tells the user to update Beam on the
  other device otherwise. Protocol-1 receivers would reject the unknown frame type.
- Receivers show text from trusted devices without asking (copied to the clipboard with a notification); otherwise they
  show it and let the user copy it or, for a single `http(s)` link, open it. Other URI schemes are never opened.

## Nearby over Bluetooth LE (Receive / Send without a shared network)

A device with **Receive** open advertises a beacon. A device with **Send** open lists the beacons it hears.
Sending to one first runs a handshake over GATT. The handshake hands the sender what a pairing QR code would: the
receiver's id, name, certificate fingerprint, addresses, and its own network's name and passphrase. From there the
sender joins that network and connects as usual (TLS, checked against the fingerprint; the receiver still approves).

**Beacon** (manufacturer data, company id `0xFFFF`, at most 20 bytes):
`"Bm"`, version `2`, flags (`1` its network is on, `2` computer, `4` can start a network, `8` takes sender-hosted
direct sessions),
the first 6 bytes of SHA-256 of the device id, a CRC-8 (polynomial 0x07) over all other bytes, then the start of the
name (UTF-8, not empty). Beacons that fail the check are ignored (garbled reads used to show up as extra devices).
Windows also advertises the GATT service separately; Android puts the service UUID in the scan response.

**GATT**: service `8b2f6a10-4c1e-4d7b-9e55-3b0e5a1c7d01`, characteristic `8b2f6a11-…` (write, then read).

1. The sender writes 73 bytes: version `1`, a fresh P-256 public key (65 bytes, uncompressed), its own 6-byte id hash,
   and flags (`1`: the sender will host, so don't start your network).
   The id hash is a hint only: it tells the receiver whether the sender is a paired device.
2. The sender reads until the answer isn't "working" (`[1, 0]`, sent while the receiver starts its network).
   A refusal is `[1, 2, message…]`. A ready answer is `[1, 1]`, then the receiver's fresh P-256 key (65 bytes),
   an IV (16), the ciphertext, and an HMAC (32).
3. Keys: ECDH → SHA-256 → HKDF-SHA256 (salt = sender key ‖ receiver key, info `beam-nearby-v1`) → 32 bytes for
   AES-256-CBC and 32 bytes for HMAC-SHA256. The HMAC covers everything before it (encrypt-then-MAC).
   The plaintext is the pairing code as JSON.

A device seen before must answer with the same fingerprint, or the sender refuses to connect.
The pairing code in the answer carries no network name or passphrase, unless the request came without flag `1` (an
older sender) and the receiver started its network for it; then, if that sender isn't paired, the receiver gives its
network a new passphrase once the network is off.

**Session messages** (sender-hosted direct sessions, below): after a ready answer, the sender writes
`[1, 0x20, length (2 bytes, big-endian), IV, ciphertext, HMAC]` with the same keys; the plaintext is a direct request
(JSON, as below) with `senderId`, `senderName` and `senderFingerprint`, whose id must match the handshake's id hash.
A proposal is answered `[1, 4]` (accepted) or `[1, 2, message…]`; an offer `[1, 3, its address on the network]` once
the receiver joined and reported in, `[1, 2, message…]` when it couldn't, and "working" until then. Over Bluetooth the
sender's identity is only claimed: the receiver always asks its user, and the TLS connections on the direct network
check the claimed fingerprint before any file comes. A join request without a session (Beam 1.5.3 and older) is
refused.

Every value written or read is at most 512 bytes (a GATT attribute). Only the proposal carries the identity fields;
offers and ends on the same connection leave them out (the receiver takes them from the proposal), and their
`message` is at most 80 characters. JSON over Bluetooth keeps non-ASCII text as UTF-8 (not `\uXXXX`); if a message
still doesn't fit, only the text shown to people (`senderName`, then `message`; in an answer, `addresses`, then the
name) is shortened. The connection of the handshake that found out who a receiver is stays open for 30 s, and a
direct session started meanwhile uses it.

## Direct sessions (sender-hosted)

Connection purpose `direct`; one `DirectRequest` (JSON) answered by one `DirectResponse` (`ok`, `message`, `declined`).
One session per receiver: `sessionId` and `nonce` are 128 random bits (hex). Devices announce support with the role
word `shost` (discovery) or beacon flag `8`.

| Action | From → to, over | Meaning |
|---|---|---|
| `propose` | sender → receiver, control channel | "Accept a direct session?" (`receiverId` must be the receiver's id). Paired senders (proven by TLS) are accepted; others only with Receive open and the user's yes. Nothing is started before this. |
| `offer` | sender → receiver, control channel | The session's `ssid`, `passphrase` (fresh per network, 20 characters), `ttlSeconds`, the sender's `port`, `hostIsComputer`. Answered at once; the receiver then joins. |
| `ready` | receiver → sender, **direct network** | "Joined; my Beam is at `port`." Accepted only from the session's certificate, with its nonce, from a direct address. |
| `verify` | sender → receiver, **direct network** | Checked the same way on the receiver. Only then may file data go, and only to that endpoint. |
| `failed` | receiver → sender, LAN | Joining failed (`stage`, `message`), so the sender can say where. |
| `end` | either way | The session is over: the receiver leaves what it joined for it, the sender gives back its share of its network. Ended ids are never accepted again. A receiver whose OS dropped the direct network sends `end` with `stage` `lost` over the shared network (retried for 45 s): the sender then sets up a new session at once instead of waiting for the transfer to time out. |

The control channel is Beam's TLS connection on the shared network when the receiver answers there (checked against
its paired, remembered or announced fingerprint), else the Bluetooth session messages above. Neither discovery
announcements nor beacons ever carry network details.

After a verified link is lost, the sender sets up a new session (new id and nonce, the receiver authenticated again)
on the network it keeps hosting, retrying for up to 2 minutes while the receiver can't be reached; the transfer then
resumes from the offsets the receiver's `OfferResponse` plan confirms.

**Reconnect proof.** Answering a `verify` (over the direct network's TLS connection, with the sender's certificate),
the receiver adds `reconnectKey`: 32 random bytes (base64), replacing any earlier key for that certificate and valid for
30 minutes. A later `propose` from that sender may carry `proof` = the first 16 bytes (base64) of
HMAC-SHA256(key, length-prefixed fields: `beam-reconnect-propose-1`, `sessionId`, `receiverId`, `senderId`,
`senderFingerprint`); its `offer` then must carry `proof` over `beam-reconnect-offer-1`, `sessionId`, `nonce`, `ssid`,
`passphrase`, `port`, `ttlSeconds`, `hostIsComputer`. A valid propose proof counts like a paired sender (no question
to the user, also with Receive closed); a wrong or expired one is ignored (the user is asked), and an offer without a
valid proof after a proven propose is refused. Session ids are single-use and the TLS check on the direct network
still follows, so a proof can't be replayed or bent towards another network. A transfer gives up after two set-ups in a row
that moved no data (30 in all). It never continues over the shared network by itself. Older versions' receiver-hosted requests (`host`, `join`,
`prefer`, `stop`) are still answered for compatibility; this version never sends them.
