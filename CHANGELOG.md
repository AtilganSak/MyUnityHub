# Changelog

All notable changes to this package are documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/);
this package uses [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Changed — breaking
- The editor UI is now entirely in English. Every menu path, button, dialog, status
  line and generated file comment was Turkish before.
- **Credentials moved out of the editor into a `.myhub` file.** Client ID, client secret,
  Drive root folder id, GitHub PAT and the Google refresh token now live in a file you
  choose; the editor persists only its path. The credential text fields on the settings
  page are gone — the page shows which values are present, not what they are.
- Opening the window with no file attached shows an attach screen: pick an existing
  `.myhub`, or build one with the new wizard.
- 1.0.0 secrets left in `EditorPrefs` are detected and can be migrated into a file or
  deleted, from a warning shown on both the attach and settings pages.
- OAuth scope narrowed from full `drive` to `drive.readonly` + `drive.file`. **Existing
  users must sign out and sign in again** to move to the narrower scope.
- The window moved from **Tools ▸ MyUnityHub** to **Tools ▸ MyUnityHub ▸ Open**. Unity
  cannot treat one menu path as both a command and a submenu parent, and the path is
  now the parent of the helper commands below.

### Added
- **Credential file wizard** — `Tools ▸ MyUnityHub ▸ New Credential File...`, also
  reachable from the attach screen. Masked fields with a reveal toggle, accepts a
  pasted Drive folder URL and extracts the id, defaults the save location outside the
  project and warns if you aim inside it, then writes the file and attaches it. Values
  go to the file only: no EditorPrefs, no serialized fields, cleared on close.
- `Tools ▸ MyUnityHub ▸ Run Credential Format Test` — self-check for the `.myhub`
  parse/upsert/build rules.
- `LICENSE.md` (MIT) and this changelog.

### Fixed
- GitHub `package.json` probing is throttled to 8 concurrent requests. Unbounded
  parallelism tripped the secondary rate limit, and the resulting 403 was silently
  read as "repo has no package.json".
- Non-404 GitHub probe failures now surface as an error instead of flagging the repo
  as a non-package.
- Async operations no longer repaint a closed editor window (`MissingReferenceException`).
- Google login runs off the main thread, matching the other network calls; the editor
  no longer freezes on a slow proxy/DNS lookup during sign-in.
- `Application.OpenURL` during OAuth is marshalled to the main thread, so login works
  when triggered from a background operation.
- Cached Drive/repo lists moved from `EditorPrefs` to `Library/MyUnityHub/`. They can be
  hundreds of KB, past what the Windows registry backing `EditorPrefs` reliably stores.
  This also drops a project key derived from `string.GetHashCode()`, which is not stable
  across runtimes.
- Import target validation rejects sibling directories such as `.../AssetsBackup`.
- Folder import "Overwrite" now deletes the existing folder first (via `AssetDatabase`,
  so `.meta` files go with it). It previously merged, leaving files deleted on Drive.
- Downloads stream to disk instead of buffering the whole file in memory.
- `HttpResponseMessage`/`HttpRequestMessage` instances are disposed.
- `GUI.enabled` is no longer assigned inside a `DisabledScope`, which broke scope restore.

## [1.0.0]

- Initial release: Google Drive import/upload and GitHub repo → UPM package install.
