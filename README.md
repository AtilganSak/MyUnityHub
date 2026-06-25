# MyUnityHub

Editor window for Google Drive + GitHub/UPM workflows.

Open: **Tools ▸ MyUnityHub**

## Features
- **Google Drive** (OAuth2 loopback login): list a root folder, import multiple files or one folder into `Assets/`, upload local files/folders back to Drive. Compile-batched (`StartAssetEditing`), conflict prompt (Overwrite/Skip/Cancel).
- **GitHub / UPM**: list your repos, filter the ones with `package.json`, add them as UPM packages via `Client.Add` (writes the git URL into `manifest.json`).
- Settings page (`⚙`), masked credential fields, persistent list cache, progress bar, busy-lock.

## Setup
1. **Tools ▸ MyUnityHub ▸ ⚙ Ayarlar**.
2. Google Cloud → **Desktop app** OAuth client → Client ID + Secret. Enable **Google Drive API**.
3. Drive Root Folder ID (from the folder URL).
4. GitHub PAT (`repo` or `public_repo`).
5. **Google ile Giriş Yap** → browser → consent.

## Install (as package)
Already embedded under `Packages/com.sakatilgan.myunityhub`. To reuse in another project, copy that folder into the target project's `Packages/`, or add via Package Manager ▸ *Add from disk* / git URL.
