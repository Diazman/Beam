# Testing Beam

## Automated tests

```bash
dotnet test Beam.sln          # ~3 minutes; runs on Windows, Linux and macOS
```

**`tests/Beam.Core.Tests`** (248 tests) start real Beam nodes in-process — real TLS, real sockets, real files — and
transfer between them over loopback. A fault-injecting TCP proxy (`FlakyProxy`) cuts connections mid-transfer.
Direct mode and Bluetooth run against fakes of the radios (`FakeDirectAir`, `FakeNearbyAir`), which can drop links,
refuse joins or turn Wi-Fi off; tests that change process-wide network state run alone.

**`tests/Beam.App.Tests`** (56 tests, including one that renders the Microsoft Store screenshots) run the actual Avalonia UI headlessly against a real node, drive it through
view-model commands while a second node sends/receives, and render screenshots of every major screen to
`artifacts/screenshots/` for visual review.

### Required checks and where they are covered

| # | Requirement | Automated coverage | Also verified manually (see below) |
|---|---|---|---|
| 1 | Two computers discover each other | `DiscoveryTests.*` (find, rename, bye, hide, expire, manual address) | Two real app processes found each other via multicast |
| 2 | Send a single file | `SendsSingleFileIntact` | ✔ |
| 3 | Send multiple files | `SendsMultipleFilesWithUnicodeNames`, `ManySmallFilesAreFast` (2,000 files) | ✔ |
| 4 | Send a folder | `PreservesFolderStructureIncludingEmptyFolders`, `MergesIntoExistingFolder…` | ✔ (nested folders, Unicode/emoji names) |
| 5 | Accept / reject | `DeclinedTransferSendsNothing`, `ReceivingAsksAcceptsAndShowsResult`, `DecliningRemovesTheCard…`, `SenderWithdrawingClosesThePrompt`, `TrustedDeviceIsAcceptedWithoutAsking` | ✔ |
| 6 | Progress displayed correctly | `SendingShowsLiveProgressAndCompletes` (size text, speed, status), `LargeFileStreams…` (progress while in flight) | ✔ ("Transferring 4 of 4 files · 149 MB of 304 MB · 129 MB/s") |
| 7 | Large files don't freeze the app | `LargeFileStreamsWithoutLoadingIntoMemory` (512 MB, managed heap < 200 MB), UI test sends 400 MB while the UI keeps updating | ✔ (1.2 GB) |
| 8 | Existing files handled | `HandlesExistingFiles(Replace/KeepBoth/Skip)`, `ConflictsCanBeResolvedForAllAtOnce` | ✔ (Replace for all) |
| 9 | Interrupted transfers fail gracefully | `ResumesAfterConnectionDrop…`, `ReceiverClosingMidTransfer…`, `ReceiverCancellingStopsSender`, `SenderCancellingMidTransfer…`, `UnreachableDevice…`, `UnavailableDestination…`, `FailedSendExplainsAndOffersRetry` | ✔ receiver killed with `kill -9` mid-transfer, restarted, transfer resumed from 445 MB automatically |
| 10 | Completed files are intact | Every transfer test compares SHA-256; `CorruptedPartialFileIsDetectedAndResent` | ✔ hashes compared |
| 11 | Close and reopen normally | `IdentitySurvivesRestart`, `SettingsPersist…`, `HistoryIsCapped…`, `FirstRunShowsWelcome…`, `QuittingDuringATransferAsksFirst` | ✔ identity, settings and history survived restarts; second launch hands off to the running instance |
| 12 | UI stays responsive | All I/O is async off the UI thread; UI tests keep rendering during a 400 MB transfer | ✔ |

Security-specific tests: `ImpostorWithWrongFingerprintIsRejected`, `NeverWritesThroughALinkedFolder`,
`SafePathTests` (traversal, rooted paths, reserved names), `RejectsMalformedPackets`, `RejectsOversizedAndUnknownFrames`,
`OnlyFileListsMayBeBigAndMemoryFollowsWhatArrives`, `DirectSecurityTests` (who may make a device start or join a
network), `AnImpostorWithADifferentCertificateIsNotTrusted` (Bluetooth), `BackupTests` (the backup code works once, expires,
ends after wrong guesses; strangers, removed phones and impostor computers get nothing; never over a direct link).

## Manual test procedure (two Windows PCs, then phones)

Automated tests run on any OS; these steps cover what only real Windows machines can show (firewall, toasts,
tray, installer, Wi-Fi/hotspot behaviour). Use two PCs (A and B), build with `./build.ps1 -Installer`.

### Setup
1. Install `BeamSetup-…-x64.exe` on both with default options (per-user). ✅ No UAC prompt appears.
2. Launch Beam on A. ✅ Welcome screen shows the computer name and the firewall note. Rename to "PC A", click
   *Get started*. ✅ Windows Firewall prompt appears; choose *Allow* (Private). Repeat on B ("PC B").

### Discovery
3. Both on the same Wi-Fi. ✅ Within ~5 s each shows the other under *Nearby computers*; with only one computer it is
   pre-selected.
4. Close Beam on B (tray → Quit). ✅ It disappears from A within a second. Reopen: ✅ reappears.
5. On B, Settings → turn off *Let nearby computers find this one*. ✅ Disappears from A. Turn back on.
6. Repeat 3 with A sharing a **Windows Mobile Hotspot** and B connected to it, and with both on **Ethernet**.
7. Fallback: on a guest network with client isolation (or block UDP 47820 in the firewall on A), on B click
   *Can't find a computer?* and type A's address from A's Settings → Connection. ✅ A appears and can receive.

### Sending and receiving
8. On A drag a single file onto the window. ✅ Drop overlay appears; file listed with size; summary reads
   "Send "file" (size) to PC B". Click **Send**.
9. On B: ✅ dialog "PC A wants to send you 1 file" with size, *Save to: Downloads*, security code. ✅ If B's window was
   minimised/closed to tray, a toast appears and the window comes forward. Click **Decline**. ✅ A shows
   "PC B declined the files."
10. Send again, **Accept**. ✅ Both cards show progress, speed, time remaining; finished card on B has *Open* / *Show in
    folder*, which work. ✅ Toast on B "Files received" if Beam isn't in front.
11. Send a folder tree with nested and empty folders and Unicode names (e.g. `Фото/2026/日本/é.txt`). ✅ Same structure on B.
12. Send a 10+ GB file. ✅ Window stays responsive (move/resize it, switch pages) and memory stays low in Task Manager.
13. Send ~5,000 small files. ✅ Completes quickly; "Transferring N of 5,000 files" counts up.
14. Send the same files again. ✅ Conflict dialog; tick *Do this for the other N conflicts*; try each of Keep both
    (`name (1).ext`), Replace and Skip.
15. Change *Save to* in the accept dialog to another drive. ✅ Files land there. Choose a nearly-full USB stick with a
    larger transfer. ✅ Dialog warns "Not enough space" and Accept is disabled.

### Interruptions
16. During a large transfer, turn Wi-Fi off on B for ~10 s, then on. ✅ A shows "Connection lost. Reconnecting…", then
    continues from where it stopped (no new prompt on B); file verifies.
17. During a large transfer, end Beam on B in Task Manager. ✅ A shows it is reconnecting; restart Beam on B within
    3 minutes → transfer resumes automatically; later → click **Try again** on A → resumes without a prompt.
18. Cancel on A mid-transfer. ✅ B shows "PC A cancelled the transfer."; no `.beampart` files remain.
19. Cancel on B mid-transfer. ✅ A shows "PC B cancelled the transfer."
20. Quit Beam on A (tray → Quit) during a transfer. ✅ Confirmation "A transfer is still in progress"; choosing
    *Keep transferring* keeps it running.
21. Send a file that is open and locked by another program (e.g. an open `.pst`). ✅ Per-file error "couldn't be
    read…" and the other files still arrive ("Finished, but 1 file couldn't be transferred"; *Details* lists it).

### App lifecycle and settings
22. Close the window (X) with default settings. ✅ One-time toast "Beam is still running"; tray icon remains; B can
    still send to A. Tray icon click restores the window.
23. Turn off *Keep running when the window is closed*; X now quits (asking first if a transfer is running).
24. Enable *Start Beam when I sign in*, sign out and in. ✅ Beam starts hidden in the tray, receiving works.
25. Change name, receive folder, theme (Light/Dark/System), notification toggles. ✅ Applied immediately and kept after restart.
26. Accept with *Always accept files from PC A*. ✅ Next transfer from A starts without asking; it's listed under
    Settings → Trusted computers and can be removed.
27. History lists every transfer with status; *Show* opens received files; *Clear history* asks first.
28. Display scaling 100 % / 150 % / 200 % and window resizing to the minimum size. ✅ Text stays readable and nothing clips.
29. Run `Beam.exe --send "C:\some\file.txt"` while Beam is running. ✅ The running window comes forward with the file added.
30. Uninstall via Settings → Apps. ✅ App, shortcuts and Run entry removed; received files untouched.
31. Installer version: right-click a file, then a folder, then 20 selected files (Windows 11: *Show more options*)
    → **Send with Beam**. ✅ Beam opens (or comes forward) with all of them in the send list. Uninstall removes the entry.
32. Store version: right-click a file → **Share** → **Beam**. ✅ Beam opens with the file in the send list; the Share
    dialog closes.
32b. Store version on Windows 11: right-click a file, a folder, and 500 selected files. ✅ **Send with Beam** (with the
    Beam icon, in the Windows language) is in the main menu, not only under *Show more options*; Beam opens (or comes
    forward) with all of them. On Windows 10 it is in the normal right-click menu.
33. Store version: after the 3rd completed transfer, the Store's rating dialog appears once (and never again).
34. Settings → **Language** → Türkçe / Русский / Oʻzbekcha, restart Beam. ✅ Every screen, dialog, toast, tray menu
    and the on/off switches are in that language; sizes use the local decimal separator ("7,63 MB"). Open the Phone
    page on a phone set to that language: ✅ the page is translated too. Back to *Use Windows language*.

### Direct mode (Settings or the Send page: *Send directly*)
35. PC ↔ PC on no shared network (one on Ethernet, the other on Wi-Fi elsewhere, or Wi-Fi off on one). Open Receive on
    B. ✅ A lists B over Bluetooth; Send directly → B asks once (unless paired) → B joins A's network → files arrive;
    both are back on their own networks afterwards.
36. A online, B a phone: ✅ A uses its Mobile hotspot (the phone stays online during the transfer).
37. Turn Wi-Fi off on the receiver mid-transfer, then on. ✅ "Reconnecting to …", a new direct session, and the transfer resumes.
38. Make the direct link impossible (out of range). ✅ The transfer fails with the stage that failed and offers
    **Send over Wi-Fi instead**; nothing went over the shared network by itself.

### Backup (a Windows PC and an Android phone on the same Wi-Fi)

1. PC: Settings → Backup → Add a phone. Phone: Settings → Backup → Set up backup, scan the code. Both say who the
   other is; the PC lists the phone.
2. Scan the same code again with another phone: refused. Close the dialog, scan a photo of the code: refused.
3. Phone: allow photos and videos, add a folder and tick only PDF (Documents shows partly ticked). Back up now: the
   PC's Documents/Beam Backups/<phone> gets Photos, Videos and Documents folders (the folder's PDFs only, without
   the phone's folders), without any question. Turn on "Keep the phone's folder names": the next backup keeps them.
4. Back up now again: nothing is sent, no card, no history entry. Take a photo: it arrives on the next run.
5. Leave the Wi-Fi, take photos, come back: the backup runs by itself within a few minutes (also with Beam closed).
6. Hand the unlocked phone to someone else: setting up a backup (or adding a folder to it) asks for the fingerprint,
   face or PIN; cancelling changes nothing. The notification "This phone now backs up to …" appears after setup, and
   "N files backed up to …" after each backup that sent files; its Stop backup button works with Beam closed.
7. Delete a photo on the phone: it stays on the PC. Remove the phone on the PC: the next backup is refused and the
   phone says to set it up again.

### Phones
39. Android: install the test APK; scan a PC's code (Receive page) to pair. ✅ Each can send to the other; Share →
    Beam from another app works; a transfer continues with the screen off (ongoing notification); Accept/Decline
    from the notification works.
40. Android sending to a PC with Send directly: ✅ the phone hosts (its hotspot on Android 11+ when supported), the
    PC joins, the PC is back on its Wi-Fi afterwards.
41. iPhone (sideloaded): ✅ iOS asks for local network access and notifications once; sending to and receiving from
    a PC on the same Wi-Fi works; with Send directly the PC starts its network and iOS asks to join; received files
    show in the Files app (On My iPhone → Beam).

## Performance notes

Measured in CI-like containers (4 vCPU, no SHA hardware acceleration, both peers on the same machine):
~105 MB/s for a single large file over loopback TLS including SHA-256 on both ends; 2,000 small files in 0.5 s.
On typical PCs with SHA extensions, gigabit Ethernet / Wi-Fi throughput is the limit.
