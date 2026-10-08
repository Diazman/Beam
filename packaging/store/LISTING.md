# Microsoft Store listing — Beam

Copy these into Partner Center > your app > Store listings > English. Screenshots are in
[`screenshots/`](screenshots) (1600×900, the Store needs at least 1366×768).

## Product name
Beam

> The name must be reserved in Partner Center. If "Beam" is already taken, reserve something like
> "Beam File Transfer" or "Beam – Send Files Nearby" and set `displayName` in
> `packaging/msix/identity.json` to the reserved name.

## Short description (≤ 100 characters, shown in search results)
Send files and folders to nearby computers in seconds. No cables, accounts, or internet needed.

## Description
Beam is the easiest way to move files between computers on the same Wi-Fi or network.

Open Beam on both computers. They find each other automatically — no IP addresses, network shares,
USB sticks or cloud uploads. Pick the other computer, drag in your files or whole folders, and
press Send. The other person sees exactly what's coming and decides whether to accept it.

FAST AND RELIABLE
• Transfers go directly between your computers, never through the internet.
• Huge files and folders with thousands of files are no problem.
• If the connection drops, Beam reconnects and continues where it stopped instead of starting over.
• Every file is checked after it arrives, so you never end up with a damaged copy.

PRIVATE AND SECURE
• Files never leave your network and never touch a server — Beam has no servers.
• Every transfer is encrypted, and nothing is received without your permission.
• No account, no sign-up, no ads, no tracking.

SIMPLE
• Folder structure, file names and dates are kept exactly as they were.
• Choose what happens when a file already exists: replace, keep both, or skip.
• See progress, speed and time remaining for every transfer.
• Runs quietly in the notification area so others can send you files any time.
• Light and dark themes.

PHONES TOO — NO APP NEEDED
• Send photos and videos from any iPhone or Android phone to your PC, and files from your PC to the phone.
• Just scan the code Beam shows with the phone's camera. The phone uses its web browser; nothing to install.

Works on Wi-Fi, wired networks and Windows Mobile Hotspot. If a network blocks automatic discovery,
connect using the address Beam shows on the other computer.

FREE AND PRO
Beam is free to use: send to one computer at a time at up to 5 MB/s, 10 times a day. Receiving is
always free and unlimited. Beam Pro, an optional one-time in-app purchase, adds:
• Send to several computers at once
• Full speed — as fast as your network allows
• Unlimited sends
• Sending to phones and tablets once Beam for Android and iPhone is available

## What's new in this version
First release.

## Product features (up to 20, one per line)
Send to and from any phone by scanning a code (no app needed)
Find nearby computers automatically
Send files and whole folders
Drag and drop
Receiver approves every transfer
Encrypted, direct computer-to-computer transfers
Resumes interrupted transfers
Verifies every file after transfer
Keeps folder structure and file dates
Replace, keep both or skip existing files
Live progress, speed and time remaining
Send to several computers at once (Pro)
Full-speed, unlimited sending (Pro)
Multiple transfers at once
Transfer history
Works without internet
Notifications when files arrive
Start with Windows
Light and dark theme

## Search terms (up to 7)
file transfer; send files; share files; LAN transfer; wifi transfer; share between PCs; local network

## Category
Productivity (subcategory: none) — alternatively Utilities & tools.

## Other listing fields
| Field | Value |
|---|---|
| Privacy policy URL | A public URL of `PRIVACY.md` (see docs/STORE-SUBMISSION.md, step 6) |
| Website | https://github.com/Diazman (or your own site) |
| Support contact | Your support e-mail address |
| Copyright | © 2026 Diazman |
| System requirements | Windows 10 version 1809 or later; a local network |
| Pricing | Free |
| Age rating questionnaire | No user-generated content shared publicly, no purchases, no location, no personal data collection → typically rated 3+ / Everyone |

## Add-on: Beam Pro
Create it in Partner Center > your app > **Add-ons > Create a new add-on**:

| Field | Value |
|---|---|
| Product type | **Durable**, product lifetime **Forever** |
| Product ID | `BeamPro` (must be exactly this — the app looks for it) |
| Title | Beam Pro |
| Description | Send to several computers at once, at full speed, as often as you like. Includes sending to phones and tablets when Beam for Android and iPhone arrives. |
| Price | your choice (e.g. USD 4.99 tier); the Store converts it to local prices, including Turkish lira |
| Store listing image | `packaging/msix/Assets/StoreLogo.scale-400.png` |
