# Building and installing the phone apps

Both phone apps share the engine (`src/Beam.Core`) and the screens (`src/Beam.UI`) with the Windows app. GitHub
Actions builds them; you can also build them yourself.

## Android

**Get a test build.** Every change on `main` publishes the newest APK on the repository's **Releases** page as
"Android test build" (`Beam-android-test.apk`). For another branch, open **Actions → Build → Run workflow**, pick the
branch, and download the `beam-android-<commit>` artifact when it finishes.

**Install it.** Open the APK on the phone and allow installing apps from that source (Android asks the first time).
Later APKs install over the previous one and keep its settings and pairings, as long as they are signed with the
same key (below).

**Signing.** The builds are signed with the key in the repository's secrets. Setting it up once is described in
[ANDROID-SIGNING.md](ANDROID-SIGNING.md). Until the secrets are added, each build is signed with a throwaway key,
and the build log shows a warning: uninstall Beam before installing such a build.

**Build it yourself.** You need the .NET 10 SDK and the Android workload (`dotnet workload install android`, which
also needs Java 17 and the Android SDK):

```
dotnet publish src/Beam.Android -c Release -f net10.0-android -o artifacts/android
```

The APK is `artifacts/android/com.diazman.beam-Signed.apk`. To sign it with your own key, add
`-p:AndroidKeyStore=true -p:AndroidSigningKeyStore=beam.keystore -p:AndroidSigningKeyAlias=beam -p:AndroidSigningStorePass=env:ANDROID_KEYSTORE_PASSWORD -p:AndroidSigningKeyPass=env:ANDROID_KEY_PASSWORD`
and set those two environment variables.

**Backups.** Settings → Backup sets up automatic backups to a computer (scan the code from the computer's Settings →
Backup → Add a phone). Android runs `BackupJobService` about every 15 minutes on Wi-Fi; Google Play's review asks
why the app reads photos and videos (the answer: to back them up to the user's own computer, chosen by the user).

**Google Play** needs an Android App Bundle instead of an APK: add `-p:AndroidPackageFormat=aab`, and sign it with
the same key. Keep that key safe: Play can't take an update signed with another one.

## iPhone and iPad

**Get a build.** Actions builds the iPhone app on macOS, which costs ten times the minutes of a Linux build, so it
runs only when something in `src/Beam.iOS` changed, or when you start **Actions → Build → Run workflow** yourself.
Download the `beam-ios-unsigned-<commit>` artifact: it contains `Beam-ios-unsigned.ipa`.

**Install it.** An iPhone only runs apps signed for it. The IPA from Actions isn't signed, so it is signed while it
is installed:

- **With a free Apple ID**: use a sideloading tool such as Sideloadly or AltStore on a Mac or PC. It signs the app
  with your Apple ID and installs it over USB. Apps signed this way stop opening after 7 days; install them again
  to renew. On the phone, trust the developer once (Settings → General → VPN & Device Management) and turn on
  Developer Mode (Settings → Privacy & Security).
- **With a paid Apple Developer account** (99 USD a year): apps run for a year, and you can use TestFlight to
  give the app to testers and later publish it on the App Store. That needs a signing certificate and a
  provisioning profile in the build, which this repository doesn't set up yet.

**Build it yourself** (on a Mac with Xcode): install the .NET 10 SDK and `dotnet workload install ios`, then

```
dotnet publish src/Beam.iOS -c Release -f net10.0-ios26.0 -r ios-arm64 -p:EnableCodeSigning=false
```

**What works on iPhone.** Sending and receiving over the same Wi-Fi, notifications, and Direct mode with a
computer: the iPhone can't start a Wi-Fi network for others, so it joins the computer's (the computer starts it,
and iOS asks before joining). iPhones don't use Bluetooth in Beam.
