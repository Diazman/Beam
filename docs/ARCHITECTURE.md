# Beam architecture

## Projects

| Project | What | Framework |
|---|---|---|
| `Beam.Core` | The engine: identity, discovery, transfers, resume, Direct mode, Bluetooth, the Phone page, settings, history. No UI. | .NET 8 |
| `Beam.UI` | The screens shared by every app (Avalonia 11, MVVM with compiled bindings): desktop window and phone layout, dialogs, translations. | .NET 8 |
| `Beam.App` | Windows host (also runs on Linux for development and tests): tray, toasts, firewall, Windows Wi-Fi Direct / Mobile hotspot, Bluetooth, Store integration. | .NET 8, Windows SDK |
| `Beam.Android` | Android host: Wi-Fi Direct groups and the phone's hotspot, Bluetooth LE, notifications, the transfer foreground service, Share → Beam. | .NET 10 (android) |
| `Beam.iOS` | iPhone/iPad host: joining a computer's direct network (NEHotspotConfiguration), notifications, the Files app folder. | .NET 10 (ios) |

Each host gives the engine its platform parts through small interfaces (`IDirectLink` for Wi-Fi links,
`INearbyRadio` for Bluetooth, `IExternalFiles` for content URIs) and the UI through `IPlatformServices` /
`IUiServices`. Tests replace them with fakes (`FakeDirectAir`, `FakeNearbyAir`, `FakePlatform`).

```
Host app (Windows / Android / iOS)            Beam.UI                          Beam.Core
┌───────────────────────────────┐   ┌──────────────────────────────┐   ┌──────────────────────────────────────────┐
│ Platform services: links,     │   │ Views ←→ ViewModels           │   │ BeamNode (composition root)               │
│ radios, notifications, files  │──→│ MainViewModel answers the     │──→│  ├─ DeviceIdentity, Settings/HistoryStore │
│                               │   │ engine's questions            │   │  ├─ DiscoveryService (UDP)                │
└───────────────────────────────┘   │ (IIncomingTransferHandler)    │   │  ├─ TransferService (TLS listener)        │
                                    └──────────────────────────────┘   │  │   Outgoing/Incoming/TextTransfer,       │
                                                                       │  │   ResumeStore                           │
                                                                       │  ├─ DirectManager (Direct sessions)       │
                                                                       │  ├─ NearbyService (Bluetooth LE)          │
                                                                       │  └─ PhoneLinkServer (Phone page, HTTP)    │
                                                                       └──────────────────────────────────────────┘
```

- **Beam.Core never references UI code.** It reports progress through `TransferSession` objects (thread-safe; the
  UI polls snapshots 4×/second and listens to state changes) and asks the user questions through
  `IIncomingTransferHandler`.
- **Threading:** all networking and disk I/O is async on the thread pool; the UI thread only renders and handles
  input. Core events are marshalled to the UI thread with `Dispatcher.UIThread.Post`. Background work that isn't
  awaited goes through `task.Forget(what)`, which logs failures.

## Discovery

`DiscoveryService` binds UDP 47820 (shared with `SO_REUSEADDR`) and every 5 s sends a ~200-byte JSON announcement
on every IPv4 adapter, both to multicast group `239.255.73.37` and to the adapter's subnet broadcast address (some
routers drop one or the other; Windows Mobile Hotspot works with both). A device unheard from for 16 s disappears;
a clean exit sends "bye" so it disappears instantly. New devices are answered by unicast so both sides appear at
once. Network changes trigger an immediate re-scan.

Fallback: **Connect by address** probes the address over TCP/TLS, learns the device's identity, remembers the
address, and keeps polling it with unicast UDP (and TCP probes if UDP is filtered). The query carries this device's
own info, so only one side needs to type an address.

A hidden device ("Let nearby devices find this…" off) sends no announcements and anonymous queries.

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
- If the connection drops, the sender retries automatically with backoff for up to 3 minutes after the last progress,
  re-reading the device's current address from discovery. After that the card offers **Try again**.
- On reconnect the sender sends the same offer with `resume: true`. If the receiver has a record for that transfer
  id *from the same certificate fingerprint* with the *same file list hash*, it auto-accepts (the user already
  did) and replies with each file's existing byte count; the sender continues from there. Both sides re-hash the
  existing prefix so the final SHA-256 still covers the whole file.
- A file failing verification is deleted and re-sent once from byte 0; a second failure is reported per file.
- Resume records expire 24 h after the transfer's last progress; expired partial files are deleted at startup.
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

## Direct mode

With **Send directly** on, the files go over a Wi-Fi link between the two devices instead of the shared network
(`DirectManager`, one `DirectSession` per receiver):

1. **Propose** over a control channel: Beam's TLS connection when the receiver is reachable on the network, else the
   encrypted Bluetooth handshake (`NearbyService`).
2. The **sender starts a network** for this session (a fresh passphrase each time): a Wi-Fi Direct group, or on a
   Windows PC with internet its Mobile hotspot, or on an Android phone sending to a computer its own hotspot.
3. It **offers** the network's name and passphrase; the receiver joins and reports **ready** over the new network.
4. The sender **verifies** peer, session and route there, and only then sends — to that verified address only.
5. **End**: the network is stopped (or shared by the next session of the same send) and the receiver leaves it.

A device that can't start a network (an iPhone) asks a computer to host instead (`HostForSender`); verification is
the same. A dropped link is set up again as a new session with a reconnect proof, and the transfer resumes. Nothing
falls back to the shared network unless the user chooses **Send over Wi-Fi instead**. Beam 1.5.3 and older used a
receiver-hosted protocol; its requests are refused with a message to update.

## Bluetooth

A device with Receive open advertises a 20-byte beacon (`NearbyBeacon`: a hash of its id, flags, the start of its
name). A sender lists it, and resolving it runs a sealed handshake over a GATT characteristic (`NearbyHandshake`:
keys from ECDH + HKDF, AES-256-CBC with HMAC-SHA256) that returns the receiver's pairing code (id, name, certificate fingerprint, roles; never a Wi-Fi
passphrase). Direct session messages then travel sealed over that connection. A device seen before must present
the same certificate.

## Phone page

`PhoneLinkServer` serves a small web page (`phone.html`) on the local network behind a random link, for phones
without the app: uploads to the PC (with approval) and downloads of what the PC shares, folders as streamed ZIP
archives (`StoredZip`, exact size up front). It stops after 30 minutes without use. These transfers are not
encrypted by Beam (plain HTTP on the LAN).

## Backups

`Backup/BackupService` (one per node) backs a phone up to a computer over the shared network.

- **Setup (once):** Settings → Backup → Add a phone on the computer shows a `PairingCode` with a `BackupToken`
  (32 random bytes, one phone, 10 minutes, dropped after 5 wrong tries, compared in fixed time). The phone connects
  to the code's certificate only (`ConnectionPurpose.Backup`), sends `BackupRequest{setup, token}`, and on success
  stores a `BackupPlan` pinned to that certificate. The computer stores a `BackupPartner` (the phone's certificate
  fingerprint and its own folder under `EffectiveBackupFolder`). Setup over a direct address is refused.
- **Runs:** the phone offers everything its sources list (`BackupManifest.Build`: the media library through
  `IBackupSources` on Android, folders through `IExternalFiles` or the file system, filtered by type) as an ordinary
  transfer with `OfferMessage.Backup` set. The receiver takes it without asking only from a partner's certificate,
  into that partner's folder; same size and time (±2 s) is skipped, anything else replaced; nothing is deleted. A
  non-partner gets `not_backup_partner`, a backup arriving over a direct address is declined.
- **When:** the computer appearing in discovery (home again), every hour while it stays, "Back up now", and on
  Android a `JobScheduler` job (`BackupJobService`, every 15 minutes on an unmetered network) that starts the node
  without a screen and calls `RunDueAsync`. Runs that find nothing new leave no history entry and no card.

## Security model

- **Identity:** each installation creates a random device id and a self-signed ECDSA P-256 certificate (RSA-2048
  fallback). The private key is stored in `identity.json` in Beam's data folder: DPAPI-encrypted (current user) on
  Windows, in the app's private storage on phones.
- **Transport:** TLS 1.2/1.3 via the OS, *both* sides present certificates. Devices advertise their
  certificate fingerprint in discovery; the sender refuses to send if the TLS peer doesn't match ("couldn't
  confirm this is really …"). The receiver sees the sender's security code (first 8 hex digits of the fingerprint),
  which the sender can read in Settings → About.
- **Consent:** nothing is received without the user accepting, unless they explicitly trusted that device's
  certificate. Resume auto-accepts only for the same certificate and the same file list.
- **Integrity:** TLS (AEAD) protects data in transit; SHA-256 per file protects against bugs, disk errors and
  resume mismatches end-to-end.
- **Exposure:** one TCP port (47821, or the next free one) and one UDP port (47820), LAN only, no admin rights. Limits:
  16 concurrent connections, 4 pending prompts (1 per sender, files and texts alike), 64 KB for any message but
  file lists (32 MB) and text (2 MB), memory that grows with what actually arrives, 15 s handshake timeout, 2 min
  approval timeout, data never exceeding the sizes the user accepted.
- **Pairing:** scanning a computer's QR code on a phone trusts its certificate; a device id claimed with another
  certificate is refused. Direct sessions check the same certificates on the direct network before any data.
- **Backups:** set up only with the computer's one-time code (someone who saw its screen) and the phone owner's
  fingerprint, face or screen lock (`IPlatformServices.ConfirmOwnerAsync`, Android's BiometricPrompt; also for adding
  sources, other file types or switching automatic backups on; 2 minutes' grace). `BackupNotices` announces each
  setup and each backup that sent files, with a Stop button that works after the app was closed; afterwards each side
  insists on the other's certificate, so a guest on the same Wi-Fi can neither take a phone's backup nor back up
  into the computer. Backups stay on the shared network (never a direct link) and land in the partner's own folder.
- **Not yet covered:** per-device blocking of request spam.
