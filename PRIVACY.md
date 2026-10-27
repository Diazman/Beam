# Beam privacy policy

_Last updated: October 27, 2026_

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
