# Beam roadmap

_Replaces the October 2026 product plan and engineering plan, whose steps up to the phone apps are done. Last
updated October 29, 2026._

## Where Beam is (1.6)

| | Status |
|---|---|
| Windows app, Microsoft Store (MSIX) and installers | Done |
| Android app | Done; test builds on the Releases page, not on Google Play yet |
| iPhone/iPad app | First version; installed by sideloading, not on the App Store yet |
| Send files, folders and text; resume; history; QR pairing; trusted devices | Done, all platforms |
| Direct mode (devices link over their own Wi-Fi, set up over Bluetooth) | Done: Windows ↔ Android, PC ↔ PC, iPhone joins a PC |
| Phone page for phones without the app | Done (Windows) |
| Right-click "Send with Beam", Share → Beam | Done |
| Languages: English, Turkish, Russian, Uzbek | Done |
| Rating prompt after the 3rd successful transfer | Done (Microsoft Store) |
| Automatic backup from an Android phone to a PC over home Wi-Fi (photos, videos, folders by file type) | Done; iPhone not yet |

## Next

| # | What | Notes |
|---|---|---|
| 1 | Google Play release | Needs the fixed signing key ([ANDROID-SIGNING.md](ANDROID-SIGNING.md)) and an App Bundle (`-p:AndroidPackageFormat=aab`) |
| 2 | App Store / TestFlight release | Needs the Apple Developer Program and signing in CI ([MOBILE-APPS.md](MOBILE-APPS.md)) |
| 3 | "Get Beam" link on the Phone page | People without the app learn about it (growth feature G1) |
| 4 | Website | One page with downloads, the privacy policy, and support contact |

## Ideas (not started)

| # | Idea |
|---|---|
| P1b | Backup from iPhone (photo library, background refresh) |
| P4 | Browse the PC's files from the phone |
| G2 | Invite a friend |
| — | Phone ↔ phone Direct (Android Wi-Fi Direct between two phones) |

Beam is free with no purchases today. If paid features come later (the earlier plan had a "Pro" tier after a free
launch), the store listings, the privacy policy and the age-rating answers have to change with them.

## Marketing (ongoing)

- Short videos (TikTok, Reels, Shorts), each showing one problem solved in under 20 seconds: USB cable vs. Beam,
  iPhone photos to a Windows PC without iTunes, a file to a friend's phone without the app, Wi-Fi drops halfway and
  Beam continues.
- Always mention the three trust points: **no account, no internet needed, files stay between your devices.** Never
  claim things Beam doesn't do: stores remove apps for misleading listings.
- Reply to every review. Monthly "what's new" post; update store screenshots when the app changes.

## What to measure (store dashboards only; no tracking in the app)

| Metric | Target |
|---|---|
| Rating | 4.5+ |
| Still using after 7 days | 30%+ |
| Crashes | Fix any within a week |

If ratings or retention are poor, stop marketing and fix the app first.

## Costs

Microsoft Partner Center: done. Google Play: 25 USD once. Apple Developer Program: 99 USD a year. Domain: about
10–15 USD a year; hosting on GitHub Pages is free.

## Risks

| Risk | Plan |
|---|---|
| Direct Wi-Fi doesn't work on some laptops or phones | Sending over the same Wi-Fi always stays available; logs show why a link failed |
| iPhones can't receive in the background (an iOS rule) | Say so in the app; keep Beam open to receive |
| Store rejection | Follow each store's rules; fix and resubmit |
