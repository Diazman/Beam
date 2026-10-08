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
