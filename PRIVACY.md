# Beam privacy policy

_Last updated: October 29, 2026_

Beam is an app for Windows, Android and iPhone/iPad that sends files directly between nearby devices. This policy explains
what Beam does with information. In short: **Beam does not collect, sell or share any personal data, has no
servers, and has no accounts, ads, analytics or tracking.**

## What Beam does not do

- Beam does not create accounts and does not ask for your name, e-mail address or any other personal details.
- Beam does not contain advertising, analytics, tracking or telemetry of any kind.
- Beam does not upload your files, file names or any other data to the internet or to the developer. There are
  no Beam servers. Transfers go directly from one of your devices to the other over your local network, or over
  a direct Wi-Fi connection between the two devices.

## Information that stays on your device

To work, Beam stores the following **only on your own device**, in its private app storage:

- The name you give your device in Beam and your settings (for example the folder received files are saved to).
- The devices you chose to trust or paired by scanning a code: their Beam name, identifier and the fingerprint of
  their encryption key, so Beam can tell it is really them. No Wi-Fi passwords are kept: each direct connection
  gets a new one.
- A device identifier and an encryption key that let your other devices recognise this one. On Windows the key is
  protected with Windows' built-in data protection.
- A history of recent transfers (direction, device name, file or folder name, size, time and result). You can
  clear it at any time in Beam (History on Windows, Transfers on Android and iPhone).
- Log files with technical details for troubleshooting, kept for 7 days. They never leave your device unless you
  choose to send them yourself ("Send log" on Android and iPhone, "Open log folder" on Windows).

Beam never keeps copies of the files you send or receive.

## Information shared with your nearby devices

When Beam is open, it announces the device's Beam name, a device identifier and its network address on your
**local network**, so that your other devices running Beam can find it. You can turn this off in Settings
("Let nearby devices find this…").

When you send files, the names, sizes and contents of the files you selected go **directly** to the device you
chose. Transfers between devices running the Beam app are encrypted. When someone sends you files, nothing is
saved unless you accept (or you chose to trust that device).

## Direct connections

A direct connection links two of your devices over Wi-Fi without a router. The device that sends starts a
Wi-Fi network for that transfer, and the receiving device joins it with a name and password made for that
transfer only. They are exchanged over Beam's encrypted connection (or Beam's Bluetooth handshake), never over the
internet.

- **Windows Mobile hotspot** – when a Windows PC that is online sends directly to a phone, Beam may turn on the
  PC's Mobile hotspot for that transfer, sharing the PC's internet with that phone only, so the phone stays
  connected. Beam turns it off again when the transfer is done.
- **Bluetooth** – Beam uses Bluetooth Low Energy only to find your nearby Beam devices and to set up a direct
  connection when there is no shared Wi-Fi network. It sends the device's Beam name and identifier, nothing else.
- **Wi-Fi and location (Windows)** – Windows lets an app see nearby Wi-Fi networks only when location access is
  allowed for it. Beam uses that only to find the direct network it was told to join and **never reads, uses or
  stores your location**.

## Backups (Android phone to a computer)

If you set it up, Beam backs up the photos, videos or folders you choose from your Android phone to your own
computer, **directly over your Wi-Fi**, never over the internet or mobile data, and never to anyone else.

- **Setting it up** – the computer shows a code (Settings → Backup → Add a phone) that the phone scans. The code
  works once, for one phone, for 10 minutes, and stops working after a few wrong tries. With it, the phone and
  the computer exchange the fingerprints of their encryption keys: from then on, the phone sends backups only to
  that computer, and the computer accepts backups only from that phone. Another device on your Wi-Fi can't take
  part in, read or impersonate either side.
- **What is sent** – the files you chose, with their names, sizes and dates, encrypted on the way and checked
  after they arrive. Only files the computer doesn't have yet (or that changed) are sent. Nothing on the computer
  is deleted when you delete it on the phone.
- **What is stored** – on the phone: which computer it backs up to (its name, identifier and key fingerprint),
  what you chose to back up, and when the last backup was. On the computer: which phones may back up (their name,
  identifier and key fingerprint), each phone's backup folder, and when it last backed up. The backed-up files are
  in that folder (Documents → Beam Backups unless you choose another one).
- **In the background** – Android runs a short check about every 15 minutes while the phone is on Wi-Fi (not
  mobile data). If the computer isn't there, nothing happens.
- **Stopping** – "Stop backup" on the phone, or "Remove" next to the phone on the computer. What was backed up
  stays on the computer until you delete it.

## Phones without the app (Phone page in a web browser)

On Windows, "Connect with the browser" on the Phone page runs a small web page on your local network that a phone
can open by scanning the code Beam shows. The link contains a random secret that changes every time, and the page
stops after 30 minutes without use. Files a phone sends are saved only if you accept them. Beam sees the phone's
local network address and the type of phone (from its browser) to show who is connecting; neither is stored or
sent anywhere. These browser transfers go directly over your Wi-Fi but are **not encrypted** by Beam, so use them
on networks you trust.

## Android permissions

Beam asks for each permission when you use the feature that needs it, and explains why first:

- **Notifications** – to tell you when someone sends you files (with Accept and Decline buttons) and when a
  transfer has finished.
- **Nearby devices** (Android 12 and later) – for direct connections between your phone and a computer (Wi-Fi
  Direct and Bluetooth), and to notice your paired computers nearby. Beam declares that it does not use this
  permission to find your location.
- **Location** (Android 10 to 12 only) – on these versions Android requires the location permission for Wi-Fi
  Direct and for seeing nearby Wi-Fi networks. Beam uses it only for that and **never reads, uses or stores your
  location**.
- **Network access, Wi-Fi state and Wi-Fi multicast** – to find your devices on the local network and transfer
  files.
- **Running in the background (foreground service) and keeping the device awake** – only while files are being
  sent or received, so a transfer doesn't stop when you switch apps or the screen turns off. Android shows an
  ongoing notification during that time.
- **Storage** (Android 10 and earlier only) – to save received files to the Downloads/Beam folder.
- **Photos and videos** (only if you back up photos or videos) – to read them for the backup to your computer.
  On Android 12 and earlier this is the storage permission. Beam asks when you set up the backup.
- **Running at startup and in the background on Wi-Fi** (only if you set up automatic backups) – so the backup
  check continues after the phone restarts. Folders you choose for backups are read with the access Android
  gives for that folder only.

Files you choose to send are opened only through Android's file picker or "Share" menu, for the files you select.

## iPhone and iPad

- **Local network** – iOS asks once whether Beam may find and connect to devices on your Wi-Fi network. Beam
  needs it to find your computers and send files to them.
- **Notifications** – Beam asks the first time it would tell you about incoming files or a finished transfer.
- **Joining a computer's Wi-Fi** – for a direct connection, Beam asks iOS to join the network the computer
  started for that transfer; iOS shows its own "Join" question. Beam leaves the network when the transfer is done.
- Files you send are opened only through the iOS file picker or the Share menu, for the files you select.
  Received files are saved in Beam's folder, which the Files app shows ("On My iPhone" → Beam).
- Beam on iPhone and iPad doesn't use Bluetooth, the camera, the photo library or your location.

## Scanning codes (Android)

To pair your phone with a computer, Beam uses the code scanner provided by Google Play services. The camera image
is processed on your phone; Beam itself has no camera permission and does not receive any images, only the text of
the scanned code. Google Play services is provided by Google under the
[Google Privacy Policy](https://policies.google.com/privacy).

## App stores

Beam is free and has no in-app purchases. The Microsoft Store version may show the Store's own "Rate and review"
dialog once; that dialog is provided by Microsoft under the
[Microsoft Privacy Statement](https://privacy.microsoft.com/privacystatement). Downloads and updates through the
Microsoft Store, Google Play or Apple's App Store are handled by those stores under their own privacy policies.

## Your choices

- Hide your device from others (Settings).
- Remove trusted or paired devices (Settings).
- Stop backups, or remove a phone that backs up to your computer (Settings → Backup).
- Clear the transfer history (History on Windows, Transfers on Android and iPhone).
- Deny or revoke permissions at any time in the phone's settings; Beam keeps working over Wi-Fi without them
  (without notifications or direct connections).
- Remove all Beam data by uninstalling Beam (on Windows, also delete its app data folder).

## Children

Beam does not knowingly collect information from anyone, including children.

## Changes

If this policy changes, the updated version will be published at the same address with a new date.

## Contact

Questions about this policy: contact the developer through https://github.com/Diazman or the support contact shown
on Beam's Microsoft Store or Google Play page.
