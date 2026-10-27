# Signing the Android app

Android installs an update only if it is signed with the **same key** as the version already on the phone. Without a
fixed key, each build from GitHub Actions got a new throwaway key, so testers had to uninstall Beam (losing settings and
pairings) before installing the next APK. Google Play also needs one fixed key.

You make the key once, keep it safe, and give it to GitHub as four secrets.

## 1. Make the key (once)

On any computer with Java installed (Android Studio includes it), run:

```
keytool -genkeypair -v -keystore beam.keystore -alias beam -keyalg RSA -keysize 4096 -validity 10000
```

It asks for a password (twice), then your name and organisation (any values are fine), and creates `beam.keystore`.

**Keep `beam.keystore` and its password somewhere safe (a password manager), and never commit it.** If it is lost, no
later APK can update the installed app: everyone has to uninstall and reinstall, and a Google Play app can't be updated
at all.

## 2. Turn the key into text

- **Windows (PowerShell):** `[Convert]::ToBase64String([IO.File]::ReadAllBytes("beam.keystore")) | Set-Clipboard`
- **macOS:** `base64 -i beam.keystore | pbcopy`
- **Linux:** `base64 -w0 beam.keystore`, then copy the output

## 3. Add the secrets on GitHub

Repository → **Settings** → **Secrets and variables** → **Actions** → **New repository secret**. Add:

| Name | Value |
|---|---|
| `ANDROID_KEYSTORE_BASE64` | the text from step 2 |
| `ANDROID_KEYSTORE_PASSWORD` | the password from step 1 |
| `ANDROID_KEY_ALIAS` | `beam` (the `-alias` from step 1) |
| `ANDROID_KEY_PASSWORD` | the same password (keytool uses one password for both unless you chose otherwise) |

The next build signs the APK with this key. The build log never shows the passwords.

## 4. Testers: one last reinstall

The first APK with the new key can't replace one signed with an old throwaway key: uninstall Beam once, then install
the new APK. From then on, each new APK installs over the last one and keeps settings and pairings.
