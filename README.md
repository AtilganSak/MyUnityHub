# MyUnityHub

Editor window for Google Drive + GitHub/UPM workflows.

## Menu
| Command | What it does |
|---|---|
| **Tools ▸ MyUnityHub ▸ Open** | the hub window |
| **Tools ▸ MyUnityHub ▸ New Credential File...** | credential file wizard |
| **Tools ▸ MyUnityHub ▸ Run Credential Format Test** | self-check for the `.myhub` parser, logs to the console |

## Features
- **Google Drive** (OAuth2 loopback login): list a root folder, import multiple files or one folder into `Assets/`, upload local files/folders back to Drive. Compile-batched (`StartAssetEditing`), conflict prompt per file (Overwrite/Skip/Cancel). Choosing Overwrite for a *folder* deletes the existing one and re-downloads, so files removed on Drive do not linger locally.
- **GitHub / UPM**: list your repos, filter the ones with `package.json`, add them as UPM packages via `Client.Add` (writes the git URL into `manifest.json`). Probing is capped at 8 concurrent requests to stay under GitHub's secondary rate limit.
- Credentials live in an external `.myhub` file, never in editor settings — see below.
- Settings page (`⚙`), persistent list cache, progress bar, busy-lock.

## The credential file (`.myhub`)
Every secret lives in a `.myhub` file that you keep wherever you like. The editor
persists only its **path** — no client secret, PAT or refresh token is ever written to
`EditorPrefs`.

Open **Tools ▸ MyUnityHub ▸ Open**. With no file attached, the window shows two buttons:

- **Select Credential File...** — attach a `.myhub` you already have.
- **New Credential File...** — open the wizard.

Nothing else in the window is reachable until a file is attached.

### The wizard
**Tools ▸ MyUnityHub ▸ New Credential File...**, or the button of the same name.

Fields for Client ID, Client Secret, Drive root folder and GitHub PAT, masked by
default with a **Show values** toggle. For the folder you can paste the whole Drive
URL — the id is extracted on save. It tells you which half of the tool a missing value
would disable, but lets you save anyway: blank fields still get their line and comment,
so the result doubles as a fill-in-later template.

**Save and Attach...** picks a location (defaulting to your home directory, not the
project — it warns if you aim inside the project), writes the file, attaches it, and
clears the fields.

Values typed in the wizard go to the file and nowhere else: no `EditorPrefs`, and the
fields are not serialized, so a domain reload wipes them too.

To change a value later, edit the file in a text editor and press **Reload** (or just
save any script — the file is re-read on every domain reload).

### Format
UTF-8 text. First line is the magic + format version. `#` starts a comment. Each entry
is `key = value`, split on the first `=`, both sides trimmed. Unknown keys are ignored,
so a file written by a newer version still loads.

```
MYUNITYHUB/1
# Google Cloud > OAuth client, application type: Desktop app
google.clientId = 1234-abc.apps.googleusercontent.com
google.clientSecret = GOCSPX-...

# from the Drive folder URL
drive.rootFolderId = 1AbC...

# repo or public_repo
github.pat = ghp_...

# written by the editor after you sign in - do not fill this in by hand
google.refreshToken = 1//0g...
```

| Key | Used for |
|---|---|
| `google.clientId`, `google.clientSecret` | Drive OAuth |
| `google.refreshToken` | written by the editor on sign-in, removed on **Sign out** |
| `drive.rootFolderId` | folder listed in the Drive tab, and the upload target |
| `github.pat` | repo listing and `package.json` probing |

The settings page shows which of these are present, never their values.

### Keep it safe
The file is plain text — its security is where you put it. **Keep it outside the Unity
project and out of version control.** Both the wizard and the settings page warn if the
file sits inside the project.

The editor writes to the file in exactly one case: storing or clearing
`google.refreshToken`. Comments, blank lines and unknown keys are preserved, and the
write goes through a temp file, so a crash cannot truncate your credentials.

## Setup
1. Google Cloud → **Desktop app** OAuth client → Client ID + Secret. Enable the **Google Drive API**.
2. Drive folder to work in — copy its URL or its id.
3. GitHub PAT (`repo`, or `public_repo` if that is enough).
4. **Tools ▸ MyUnityHub ▸ New Credential File...**, paste all three in, **Save and Attach...**.
5. **⚙ Settings ▸ Sign in with Google** → browser → consent.

## Drive permissions
Login requests `drive.readonly` (list and download folders you already own) plus
`drive.file` (create the files and folders this tool uploads). Full read-write access
to your whole Drive is not requested.

## Cache location
Fetched Drive and repo lists are cached in `<project>/Library/MyUnityHub/`. Deleting
that folder just forces a refresh.

## Upgrading from 1.0.0
**Credentials.** 1.0.0 kept them in `EditorPrefs` (Windows registry / macOS plist). If
any are still there, the window warns and offers **Move to File and Erase** (write them
into a `.myhub` and attach it) or **Erase Only**. Both erase the `EditorPrefs` entries.

**Menu.** The window moved from **Tools ▸ MyUnityHub** to **Tools ▸ MyUnityHub ▸ Open**,
to make room for the helper commands.

**Drive scope.** 1.0.0 requested full `drive`. An existing session keeps that broad
scope until you **Sign out** and sign in again.

**List cache.** Also moved out of `EditorPrefs`, into `Library/MyUnityHub/`. Nothing to
do; the first refresh rebuilds it.

## Install (as package)
Package name: `com.heatinteractive.myunityhub`. Add it via Package Manager ▸
*Add package from git URL* with this repo's git URL, or copy the folder into the
target project's `Packages/`.

## License
MIT — see [LICENSE.md](LICENSE.md).
