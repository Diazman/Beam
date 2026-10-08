# Beam — engineering plan (changes to the app)

_Companion to [PLAN.md](PLAN.md). Step numbers (1.1, 4.3, …) match the stages there. Created October 8, 2026._

This document lists the code changes needed for each stage, so that work can be split into small,
testable pull requests. Every change keeps the existing guarantees:
- encrypted, verified, resumable transfers;
- received files never written outside the chosen folder;
- no tracking.

---

## 0. Principles for all changes

- **One protocol for every device.** Phones and PCs speak the same encrypted Beam protocol
  (`docs/PROTOCOL.md`). New abilities are added as new message fields or frame types, and the
  **protocol version goes up** (`AppInfo.ProtocolVersion`). Older peers keep working: a feature is used only
  when the other side's version supports it.
- **Shared code first.** Discovery, encryption, transfer, resume, history, settings and licensing live in
  `Beam.Core` and are reused by Windows, Android and iPhone. Platform code only covers what the operating
  system forces: files, notifications, background work, purchases and permissions.
- **Every pull request** includes:
  - unit or integration tests in `tests/`;
  - updated docs;
  - a version bump, so the Release workflow builds it;
  - green Windows CI.
- **Things that need real hardware get a written manual test checklist** in `docs/TESTING.md`: phones,
  Wi-Fi Direct, purchases, background behaviour.

---

## Stage 1 — Windows launch-ready

### 1.1 Free launch (no limits) and founding users
- `Beam.Core/Licensing/Edition.cs`: add a `LaunchPeriod` switch.
  - While it's on, the free edition gets every Pro feature (speed, unlimited sends, several devices at once).
  - Keep `FreeLimits` in the code for the later switch to freemium.
- **Founding users get lifetime Pro the store-native way.** During launch, the Microsoft Store add-on
  `BeamPro` is priced **Free** (confirm in Partner Center that a free price tier is available for the add-on when setting it up). The app shows "Get Beam Pro free — limited time", and every user who claims
  it owns it forever. When freemium starts, give the add-on a price; the earlier owners keep it.
  - Code: Upgrade dialog and Settings text adapt to a $0 price ("Claim free").
  - Tests: UI test for the "claim free" flow with `FakeStore`.
  - Note: Google Play can't sell items at $0, so on Android founding users get promo codes instead (Stage 6).

### 1.2 "Send with Beam" in the right-click menu (F3)
- **Installer version (Inno Setup):**
  - Register `HKCU\Software\Classes\*\shell\SendWithBeam` and `Directory\shell\SendWithBeam` →
    `Beam.exe --send "%1"`.
  - Several selected files start several commands; the single-instance pipe already merges them into one
    send list. Add a 300 ms "collect" window so they show up as one batch.
  - Remove the keys on uninstall.
- **Store version (MSIX):**
  - Add the `windows.shareTarget` extension so Beam appears in Windows' **Share** dialog (right-click →
    Share). On activation, read the shared files via `AppInstance.GetActivatedEventArgs()` and add them to
    the send list.
  - Later: a Windows 11 modern-menu entry needs an `IExplorerCommand` COM component. This is a separate,
    small project (C# COM server in the package); do it only if users ask.
- **Tests:** command-line merging test; manual check in installer and MSIX builds.

### 1.3 Rating prompt (G3)
- **Settings:** add `SuccessfulTransfers` and `RatingPromptShown` to `AppSettings`.
- **When to ask:** after the 3rd completed transfer, when no transfer is running and no dialog is open,
  call `StoreContext.RequestRateAndReviewAppAsync()` (Store version only). Never ask again.
- **Installer version:** no prompt (it can't open a Store review).
- **Tests:** counter logic unit test; `IStoreService.RequestReviewAsync` faked in the UI tests.

### 1.4 Languages: Turkish, Russian, Uzbek (F7) — done in v1.2.0
- Every user-visible string goes through `L.T` / `L.Plural` (C#) or `{l:T '…'}` (XAML); the English text is the key and
  `src/Beam.Core/Localization/{tr,ru,uz}.json` hold the translations (simpler to review than resx, and nothing can be
  left blank: missing text falls back to English). See [TRANSLATING.md](TRANSLATING.md).
- Plurals per language (Russian one/few/many); sizes, numbers and dates follow the chosen language.
- Settings → **Language** ("Use Windows language" by default; applies after a restart).
- Also translated: the phone page (by the phone browser's language), error messages, Store listings
  (`packaging/store/LISTING.*.md`; the MSIX manifest declares the languages so Partner Center accepts them).
- Tests: `LocalizationTests` (every text translated, placeholders match, no leftovers), `LanguageUiTests`
  (screens in each language, also at the minimum window size). Still to do: a native speaker reviews the Turkish and
  Uzbek text.

### 1.5 Send text, links and clipboard (F6)
- **Protocol v2:** the Offer can carry a `Text` item (up to 1 MB). Only send it to peers announcing protocol ≥ 2.
- **Sender UI:** "Paste text" / "Send clipboard" button on the Send page; Ctrl+V on the page pastes files or text.
- **Receiver UI:** prompt shows the text with **Copy** and **Open link** (for URLs). Trusted devices skip
  the prompt, and the text is copied to the clipboard with a notification.
- **History:** text items are stored locally with a short preview.
- **Tests:** protocol round trip, a v1 peer ignoring text, UI flow.

---

## Stage 2 — Website (2.2)
- **Location and hosting:** static site in a separate public repository (`beam-website`), published on GitHub
  Pages with the custom domain. It needs no server and no cookies.
- **Pages:**
  - Home (pitch, demo video, download buttons per platform);
  - FAQ;
  - Privacy policy (moved from the gist);
  - Help (firewall, "can't find device").
  - All pages in 4 languages.
- **"Get Beam" links** detect the visitor's device and point to the right store.

---

## Stage 4 — Android app

### 4.0 Restructure the solution (before Android code)
- **Split `Beam.App` into:**
  - `Beam.UI`: shared views, view models and styles;
  - `Beam.Desktop`: Windows/Linux host, tray, MSIX bits;
  - `Beam.Android`: Android host.
- **Mobile layout:** Avalonia uses a single-view lifetime on phones. Add a `MainView` with bottom navigation
  (Send · Received · Devices · Settings) that reuses the existing view models. The desktop keeps `MainWindow`.
- **Move platform-specific Core code behind interfaces:**
  - `ISecretProtector`: Windows DPAPI, Android Keystore, iOS Keychain.
  - **Storage abstraction (`ISendSource`, `IReceiveSink`).** The engine works with file paths today, but
    Android (and iOS) hand out **content URIs and streams**, not paths. Sources supply name, size, modified
    time and an `OpenRead()` stream; sinks create a partial item, write, verify, then publish it under its
    final name.
    - The Windows implementation stays path-based, so behaviour is unchanged.
- **Tests:** the existing 99 tests must pass unchanged after the refactor. Add tests for the stream-based
  source and sink.

### 4.1 Discovery and transfer on Android
- **Discovery:** hold a `WifiManager.MulticastLock` while Beam is open or receiving (Android drops broadcast
  packets otherwise). Ask for the `NEARBY_WIFI_DEVICES` / local network permissions as required by Android
  13+ and explain why.
- **Add mDNS/DNS-SD (`_beam._tcp`) alongside the current UDP discovery on every platform.** iPhone needs it
  (Stage 7), and it makes discovery more reliable on networks that block broadcast. Windows publishes it too.
- **Identity:** same certificate-based identity; key protected by Android Keystore.
- **Received files:** saved to `Downloads/Beam` through `MediaStore` (visible in Files and Gallery). Partial
  files are written with `IS_PENDING=1` and published only after verification, so the "never a damaged file"
  rule holds.

### 4.2 History, trusted devices, QR pairing (F4)
- **History and trusted devices:** reuse `HistoryStore` and the trusted-devices settings.
- **QR pairing:**
  - The PC shows a QR code with its device ID, certificate fingerprint and addresses (protocol v2
    `PairRequest`).
  - The phone scans it with the camera (ZXing.Net on CameraX frames) and connects.
  - **Both sides confirm the same 6-digit code** on screen, then store each other as trusted.
  - After pairing, transfers between them are auto-accepted (can be switched off per device).
- **Tests:** pairing protocol tests in Core (wrong fingerprint is rejected, codes must match).

### 4.3 Share → Beam (F3), text/links (F6)
- Intent filters for `ACTION_SEND` / `ACTION_SEND_MULTIPLE` (any type) open Beam's device picker with those
  items (content URIs → `ISendSource`).
- Text/links shared from other apps use the v2 text item.

### 4.4 Background receiving (F5)
- A **foreground service** (`dataSync` type, with Android 14 permissions) runs while receiving is enabled and
  shows a small persistent notification ("Beam is ready to receive").
- Incoming requests show a notification with **Accept / Decline** buttons. Trusted devices are accepted
  automatically.
- Users can turn background receiving off in Settings. Explain battery-optimisation exemptions only if users
  hit problems.

### 4.5 Translations and rating prompt
- Same resources as Windows. Rating prompt via Google Play In-App Review API (binding NuGet).

### 4.6–4.9 Build, signing, publishing
- **CI:** GitHub Actions job installs the .NET Android workload and builds an **APK** (for testers on GitHub
  Releases) and an **AAB** (for Play).
- **Signing:** upload key stored as GitHub secrets (Diazman creates it once; instructions in `docs/ANDROID.md`).
  Use Play App Signing.
- **Play requirements:** target the latest required API level, Data safety form ("no data collected"),
  content rating, 12-tester closed test.

---

## Stage 5 — Growth features

### 5.4 Web link for people without Beam (G1)
- The current Phone page becomes "Send to someone without Beam". It still shows a QR/link, but the phone page
  gains a **"Get Beam — it's free"** banner with store links (website detects platform).
- The PC and Android apps both offer it ("They don't have Beam? Share a link instead").

---

## Stage 6 — Pro features and freemium

### 6.1 Automatic photo/video backup (P1)
- **Phone setting:** "Back up photos and videos to [paired PC]". Optional "only while charging".
- **When to run:** Android `WorkManager` job, plus a foreground service while running. It starts when the
  paired PC is **discovered on the network**, so no Wi-Fi name check (and no location permission) is needed.
- **What to send:** query `MediaStore` for items newer than the last backed-up item. Send them with a new
  Offer purpose `backup`, so the PC auto-accepts from that trusted phone.
- **PC side:**
  - Saves to `Pictures/Beam Backup/<phone name>/YYYY/MM`.
  - Never asks, never overwrites.
  - Settings page shows the last backup time and size.
- **Tests:**
  - Core: backup purpose is accepted only from paired devices, and folders are created by date.
  - Manual: 1,000-photo first backup, then incremental, plus interruptions.

### 6.2 Direct connection with automatic fallback (P2) — Beta
- **Connection order:**
  1. Known LAN addresses / discovery.
  2. If not reachable within ~3 s, and both devices support it, start a **direct link**, then retry the same
     TLS connection over the new link.
- **Finding each other without a shared network:** paired devices advertise over **Bluetooth Low Energy**
  (Windows `BluetoothLEAdvertisementPublisher`, Android `BluetoothLeAdvertiser`). The ad carries a short,
  rotating ID derived from the pairing, so strangers learn nothing.
- **Links:**
  - **Android ↔ PC:** Wi-Fi Direct (Windows `WiFiDirectDevice`, Android `WifiP2pManager`).
  - **iPhone ↔ PC:** Windows Wi-Fi Direct "legacy" mode creates a small Wi-Fi network
    (`WiFiDirectAdvertisementPublisher` with `LegacySettings`). The iPhone joins it via
    `NEHotspotConfiguration` (one system prompt).
  - **Phone ↔ phone:** Wi-Fi Aware (Android `WifiAwareManager`, iOS 26 Wi-Fi Aware framework).
- **UI:** "Connected directly" badge on the transfer card. If the direct link fails, transfers continue over
  the normal network without user action.
- **Tests:** connection-orchestrator unit tests with fake transports. Manual matrix across laptops (Intel,
  Realtek, MediaTek Wi-Fi) and phones, documented in `docs/TESTING.md`.

### 6.3 Browse PC files from the phone (P4)
- **Opt-in on the PC:** "Let my paired phones browse these folders: …". Nothing is shared by default.
- **Protocol v3:** `ListFolder` / `Fetch` requests, allowed only from paired devices and only inside the
  shared folders (same path-safety checks as receiving). Read-only.
- **Phone UI:** a "Computers" tab with a folder browser, preview of photos/PDFs, and download.
- **Tests:** path-escape attempts, unpaired device refused, folder not shared refused.

### 6.4 Subscriptions, lifetime and regional prices
- **`IStoreService`:** supports several products: `pro_monthly`, `pro_yearly` (subscriptions) and
  `pro_lifetime` (durable). Pro is active if any of them is owned.
  - **Windows:** Store subscription add-ons + the existing `BeamPro` durable (kept as "lifetime").
  - **Android:** Google Play Billing (binding NuGet); verify purchases on the device. No server needed for
    now; add server-side verification only if fraud becomes a problem.
- **Upgrade dialog:** shows all three options with local prices from the store.

### 6.5 Ads in the free Android app (P5)
- Google Mobile Ads (AdMob) banner on the main screen only, never during a transfer.
- Non-personalized ads + Google's consent form (UMP) for EEA/UK users. Hidden when Pro is active.
- **Update the privacy policy and the Play Data safety form** (the ad SDK collects device data).
- Keep the Windows app ad-free.

### 6.6 Invite a friend (G2)
- "Invite" shares the website link with a referral code.
- Rewards use **store promo/offer codes** (Google Play promo codes, Apple offer codes, Microsoft promo codes),
  sent manually at first. Automate only if it becomes popular. Store rules don't allow unlocking paid
  features outside their purchase systems.

### 6.7 Switch to freemium
- Turn `LaunchPeriod` off in a release.
- Price the Windows `BeamPro` add-on.
- Free core transfers stay unlimited (only Pro features are locked).

---

## Stage 7 — iPhone app
- **Project:** `Beam.iOS` host (Avalonia iOS), built on GitHub's macOS runners. Certificates and provisioning
  profile are stored as GitHub secrets; upload to TestFlight from CI.
- **Discovery:** mDNS/Bonjour (from 4.1). Info.plist: `NSLocalNetworkUsageDescription` and
  `NSBonjourServices`. Optionally request Apple's multicast entitlement for the UDP discovery.
- **Storage:** received files go to the app's Documents folder, visible in the **Files** app (`UIFileSharingEnabled`).
- **Sending:**
  - pick from Photos (`PHPickerViewController`) and Files;
  - a **Share extension** so "Share → Beam" works from other apps (separate small extension target).
- **Background:** iOS only lets apps receive while open (or for a short time after). Say so clearly in the
  app; finish transfers that are already running using background task time.
- **Purchases:** StoreKit 2 (subscriptions + lifetime).
- **Direct mode:** join the PC's direct network (`NEHotspotConfiguration`); Wi-Fi Aware with other iPhones and
  Android (Beta, iOS 26+). Wi-Fi Aware may need a small native Swift module if no .NET binding exists.

---

## Cross-cutting work

| Area | Change |
|---|---|
| Protocol | Version 2 (text items, pairing), 3 (browse/fetch, backup purpose). Document each in `docs/PROTOCOL.md`; keep compatibility tests with v1 peers. |
| Security | Security review before each protocol change. Pairing codes and BLE IDs never reveal device identity to strangers. |
| CI | Windows, Android and iOS build jobs; Release workflow attaches APK/AAB and uploads to TestFlight. |
| Tests | Core tests run on every platform's runtime where possible; manual hardware checklists in `docs/TESTING.md`. |
| Crash reports | Use the stores' built-in crash reports (Partner Center, Play Console, App Store Connect). No third-party tracking. |
| Docs | `ARCHITECTURE.md` updated for the new project split; `ANDROID.md` and `IOS.md` for build/signing/publishing. |

---

## Order of pull requests

1. 1.1 Launch period + "claim free Pro"
2. 1.3 Rating prompt
3. 1.2 "Send with Beam" (installer) + Share target (Store)
4. 1.5 Text/links (protocol v2)
5. 1.4 Translations
6. 4.0 Project split + storage abstraction + mDNS
7. 4.1–4.5 Android app (several PRs: discovery/transfer → history/pairing → share intent → background → polish)
8. 4.6 Android CI, signing, Play release
9. 5.4 Web link with "Get Beam"
10. 6.1 Photo backup
11. 6.4 Subscriptions/lifetime · 6.5 Ads · 6.6 Invites
12. 6.3 Browse PC files
13. 6.2 Direct connection (Beta)
14. 7.x iPhone app
15. 6.7 Switch to freemium (when Diazman decides)
