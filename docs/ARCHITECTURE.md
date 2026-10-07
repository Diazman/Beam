# Beam architecture

## Technology choices

| Decision | Choice | Why |
|---|---|---|
| Runtime | .NET 8 (LTS), C# | Fast streaming I/O, first-class TLS (SChannel on Windows), single-file self-contained publishing, mature tooling. |
| UI | Avalonia 11 + Fluent theme, MVVM with compiled bindings | Modern Windows-11-style UI with per-monitor DPI; XAML binding errors fail the build; the same UI can later ship on macOS/Linux; the UI can be rendered headlessly in automated tests. |
| Distribution | One self-contained, trimmed, ReadyToRun `Beam.exe` (~31 MB) + Inno Setup installer | No .NET install needed, fast startup, per-user install without admin rights. |
| Dependencies | Avalonia (MIT), `System.Security.Cryptography.ProtectedData` (Microsoft) | Nothing else — MVVM helpers, JSON (source-generated), logging and protocol are in-house and small. |

## Components

```
Beam.App (Windows desktop, Avalonia)                    Beam.Core (no UI, cross-platform)
┌──────────────────────────────────────┐                ┌────────────────────────────────────────────┐
│ Views (XAML)  ←→  ViewModels         │                │ BeamNode  — composition root used by apps   │
│   MainWindow, Home, History,         │   IIncoming-   │  ├─ DeviceIdentity  (id + TLS certificate)   │
│   Settings, TransferCard, dialogs    │   Transfer-    │  ├─ SettingsStore / HistoryStore (JSON)      │
│ MainViewModel implements ────────────┼── Handler ───→ │  ├─ DiscoveryService (UDP)                   │
│   approval + conflict prompts        │                │  ├─ TransferService (TCP+TLS listener)       │
│ Platform/ (Windows: toasts, Run key, │                │  │    ├─ OutgoingTransfer (sender)            │
│   Explorer; generic fallback)        │                │  │    ├─ IncomingTransfer (receiver)          │
│ Services/ (single instance, CLI)     │                │  │    └─ ResumeStore (partial-transfer state) │
└──────────────────────────────────────┘                │  └─ Licensing/IEditionPolicy                 │
                                                         └────────────────────────────────────────────┘
```

- **Beam.Core never references UI code.** It reports progress through `TransferSession` objects (thread-safe; the
  UI polls snapshots 4×/second and listens to state changes) and asks the user questions through
  `IIncomingTransferHandler`. A phone app, a CLI, or an Explorer shell extension can host the same engine.
- **Threading:** all networking and disk I/O is async on the thread pool; the UI thread only renders and handles
  input, so big transfers never freeze the window. Core events are marshalled to the UI thread with
  `Dispatcher.UIThread.Post`.

## Discovery

`DiscoveryService` binds UDP 47820 (shared with `SO_REUSEADDR`) and every 5 s sends a ~200-byte JSON announcement
on every IPv4 adapter, both to multicast group `239.255.73.37` and to the adapter's subnet broadcast address (some
routers drop one or the other; Windows Mobile Hotspot works with both). A device unheard from for 16 s disappears;
a clean exit sends "bye" so it disappears instantly. New devices are answered by unicast so both sides appear at
once. Network changes trigger an immediate re-scan.

Fallback: **Connect by address** probes the address over TCP/TLS, learns the device's identity, remembers the
address, and keeps polling it with unicast UDP (and TCP probes if UDP is filtered). The query carries this device's
own info, so only one side needs to type an address.

A hidden device ("Let nearby computers find this one" off) sends no announcements and anonymous queries.

## Transfer protocol

See [PROTOCOL.md](PROTOCOL.md). Summary: one TCP connection per transfer, wrapped in mutually-authenticated TLS;
length-prefixed frames; the receiver answers an offer with a per-file *plan* (what to send and from which byte);
the sender streams files with SHA-256 footers; the receiver verifies each file before moving it into place.

### Sender pipeline

Reading + hashing the next 256 KB chunk overlaps with encrypting/sending the current one (double buffering);
hashing happens on the read side so the network never waits for SHA-256. On the receiver a bounded write-behind
queue (8 chunks) lets the network keep flowing while the disk writes. Memory stays constant regardless of file size.

### Reliability and resume

- The receiver writes to `name.<transferId>.beampart` and persists a `ResumeRecord` (destination, per-file target
  paths, conflict decisions, completion state) in `%LOCALAPPDATA%\Beam\resume`.
- If the connection drops, the sender retries automatically with backoff (≈30 s total), re-reading the device's
  current address from discovery. After that the card offers **Try again**.
- On reconnect the sender sends the same offer with `resume: true`. If the receiver has a record for that transfer
  id *from the same certificate fingerprint* with the *same file list hash*, it auto-accepts (the user already
  did) and replies with each file's existing byte count; the sender continues from there. Both sides re-hash the
  existing prefix so the final SHA-256 still covers the whole file.
- A file failing verification is deleted and re-sent once from byte 0; a second failure is reported per file.
- Resume records expire after 24 h; expired partial files are deleted at startup.
- A source file that changes size after being selected is reported as a per-file failure rather than producing a
  silently inconsistent copy.

### Receiving safely

- Every incoming relative path is split and sanitised (`SafePath`): no rooted paths, `.`/`..`, invalid Windows
  characters, reserved device names (`CON`, `NUL`, …), trailing dots/spaces; the final path is verified to be
  inside the destination. Existing symbolic links/junctions are never merged into.
- Folders merge into existing folders; a folder whose name is taken by a file gets a ` (1)` suffix; existing files
  become conflicts (Replace / Keep both / Skip). Replace writes the new file fully, verifies it, then atomically
  replaces the old one, so a failed transfer never destroys the existing file. A name that appears during the
  transfer is never overwritten.
- Free space is checked before accepting (and shown in the dialog); a full disk mid-transfer stops cleanly, keeps
  the partial data for resume, and tells the sender why.

## Security model

- **Identity:** each installation creates a random device id and a self-signed ECDSA P-256 certificate (RSA-2048
  fallback). The private key is stored DPAPI-encrypted (current user) in `%LOCALAPPDATA%\Beam\identity.json`.
- **Transport:** TLS 1.2/1.3 via the OS (SChannel), *both* sides present certificates. Devices advertise their
  certificate fingerprint in discovery; the sender refuses to send if the TLS peer doesn't match ("couldn't
  confirm this is really …"). The receiver sees the sender's security code (first 8 hex digits of the fingerprint),
  which the sender can read in Settings → About.
- **Consent:** nothing is received without the user accepting, unless they explicitly trusted that device's
  certificate. Resume auto-accepts only for the same certificate and the same file list.
- **Integrity:** TLS (AEAD) protects data in transit; SHA-256 per file protects against bugs, disk errors and
  resume mismatches end-to-end.
- **Exposure:** one TCP port (47821, or the next free one) and one UDP port (47820), LAN only, no admin rights. Limits:
  16 concurrent connections, 4 pending prompts (1 per sender), 32 MB offer size, 15 s handshake timeout, 2 min
  approval timeout, data never exceeding the sizes the user accepted.
- **Not yet covered** (future work): verified pairing (QR/PIN) so a spoofed device *name* can't trick a sender,
  per-device blocking of request spam.

## Extension points (future roadmap)

| Future feature | Where it plugs in |
|---|---|
| Phone apps | Implement the protocol (PROTOCOL.md) or host `Beam.Core` (.NET MAUI); `DeviceKinds.Phone` already exists. |
| QR pairing / trusted pairing | `TrustedDevice` + fingerprints already exist; a QR code would encode address + fingerprint for `AddDeviceByAddressAsync`. |
| Explorer "Send with Beam" | `Beam.exe --send "%1"` already works and forwards to the running instance; add a registry verb in the installer. |
| Transfer queues, pause/resume | Sessions are independent objects with `Resume()`; pause = cancel-keeping-partials + resume. |
| Internet transfers / links | Add a relay transport behind `SecureTransport.ConnectAsync`; the frame protocol is transport-agnostic. |
| Premium edition | `IEditionPolicy` / `Feature` gate capabilities; `FreeEdition` enables everything today. |
| Privacy-respecting analytics | A single opt-in sink would subscribe to `TransferService.SessionFinished`; nothing is collected today. |
