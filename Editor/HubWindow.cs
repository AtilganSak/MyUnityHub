using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace MyUnityHub
{
    /// <summary>
    /// Drive + GitHub hub. Tab 1: import/upload scripts & folders to/from Google
    /// Drive. Tab 2: list GitHub repos and add UPM packages by git URL.
    /// </summary>
    public class HubWindow : EditorWindow
    {
        // No credential ever lives in EditorPrefs. Everything comes from the attached
        // .myhub file (see CredentialStore); the editor remembers only its path.
        Credentials _creds;
        string _credsError;

        // ---- runtime state -------------------------------------------------
        enum Tab { Drive, GitHub }
        enum View { Main, Settings }
        Tab _tab;
        View _view;
        string _status = "";
        Vector2 _scroll;
        string _search = "";

        bool _busy;                 // an op is running -> whole window disabled
        int _progDone, _progTotal;  // _progTotal==0 => indeterminate (animated bar)

        GoogleDriveClient _drive;
        GitHubClient _github;

        readonly SimpleCache<List<DriveFile>> _driveCache = new SimpleCache<List<DriveFile>>(60);
        readonly SimpleCache<List<Repo>> _repoCache = new SimpleCache<List<Repo>>(120);

        List<DriveFile> _driveItems;
        readonly HashSet<string> _selectedFiles = new HashSet<string>(); // file ids
        string _selectedFolderId; // single-select folder

        List<Repo> _repos;

        // Unity cannot have "Tools/MyUnityHub" be both a command and the parent of the
        // helper items, so everything lives one level down. Priority gaps of 10+ draw
        // separators between the groups.
        [MenuItem("Tools/MyUnityHub/Open", false, 0)]
        public static void Open() => GetWindow<HubWindow>("MyUnityHub");

        void OnEnable()
        {
            _view = View.Main;
            ReloadCredentials();
            LoadCachedLists();
        }

        /// <summary>Pull every open hub window back in sync after something outside it
        /// (the wizard) changed which file is attached.</summary>
        internal static void ReloadAllOpen()
        {
            foreach (var w in Resources.FindObjectsOfTypeAll<HubWindow>())
            {
                w.ReloadCredentials();
                w.SafeRepaint();
            }
        }

        /// <summary>Re-read the .myhub file. Runs on every domain reload, so an edit to
        /// the file is picked up by saving a script or hitting 'Reload'.</summary>
        void ReloadCredentials()
        {
            _drive = null; _github = null; // clients hold a copy of the old secrets
            _creds = null;
            _credsError = null;
            if (!CredentialStore.IsAttached) return;
            try { _creds = CredentialStore.Load(); }
            catch (System.Exception e) { _credsError = e.Message; }

            // A valid but still-empty file (a fresh template) is best explained on the
            // settings page, where the missing keys are listed.
            if (_creds != null && !_creds.HasGoogle && !_creds.HasGithub) _view = View.Settings;
        }

        // ---- persistent list cache (survives editor restart / domain reload) ----
        // Library/ instead of EditorPrefs: the lists can be hundreds of KB, which is
        // past what EditorPrefs (Windows registry) reliably stores, and Library is
        // already per-project and not version-controlled.
        static string CacheDir =>
            Path.Combine(Path.GetDirectoryName(Application.dataPath), "Library", "MyUnityHub");

        static string CachePath(string name) => Path.Combine(CacheDir, name + ".json");

        static void WriteCache(string name, string json)
        {
            Directory.CreateDirectory(CacheDir);
            File.WriteAllText(CachePath(name), json);
        }

        static string ReadCache(string name)
        {
            string p = CachePath(name);
            return File.Exists(p) ? File.ReadAllText(p) : null;
        }

        void LoadCachedLists()
        {
            var d = ReadCache("driveItems");
            if (!string.IsNullOrEmpty(d))
            {
                var w = JsonUtility.FromJson<DriveFileList>(d);
                if (w?.files != null) _driveItems = w.files.ToList();
            }
            var r = ReadCache("repos");
            if (!string.IsNullOrEmpty(r))
            {
                var w = JsonUtility.FromJson<RepoArrayWrap>(r);
                if (w?.items != null) _repos = w.items.ToList();
            }
        }

        void SaveDriveItems() =>
            WriteCache("driveItems", JsonUtility.ToJson(new DriveFileList { files = _driveItems.ToArray() }));

        void SaveRepos() =>
            WriteCache("repos", JsonUtility.ToJson(new RepoArrayWrap { items = _repos.ToArray() }));

        void OnDisable()
        {
            EditorApplication.update -= SafeRepaint; // drop the animation pump
        }

        // An in-flight op can outlive the window (user closes it mid-download); the
        // continuations must not touch a destroyed EditorWindow.
        void OnDestroy() => _closed = true;
        bool _closed;

        void SafeRepaint()
        {
            if (_closed || this == null)
            {
                EditorApplication.update -= SafeRepaint;
                return;
            }
            Repaint();
        }

        // ---- busy / progress ----------------------------------------------
        // total>0 => real fraction; total==0 => indeterminate, animated each frame.
        void BeginBusy(int total)
        {
            _busy = true; _progTotal = total; _progDone = 0;
            EditorApplication.update += SafeRepaint; // pump repaints so the bar animates
            SafeRepaint();
        }

        void EndBusy()
        {
            _busy = false; _progTotal = 0; _progDone = 0;
            EditorApplication.update -= SafeRepaint;
            SafeRepaint();
        }

        GoogleDriveClient Drive => _drive ??= new GoogleDriveClient(_creds);
        GitHubClient GitHub => _github ??= new GitHubClient(_creds.GithubPat);

        // ===================================================================
        void OnGUI()
        {
            // Nothing works without the credential file, so that screen wins over
            // every other view.
            if (_creds == null) { DrawAttachPage(); return; }
            if (_view == View.Settings) { DrawSettingsPage(); return; }

            // top bar
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                GUILayout.Label("MyUnityHub", EditorStyles.boldLabel);
                GUILayout.FlexibleSpace();
                if (Drive.HasSavedLogin) DrawGreenBadge("● Drive connected");
                using (new EditorGUI.DisabledScope(_busy))
                    if (GUILayout.Button("⚙ Settings", EditorStyles.toolbarButton, GUILayout.Width(90)))
                        _view = View.Settings;
            }

            // everything below is locked while an operation runs
            using (new EditorGUI.DisabledScope(_busy))
            {
                _tab = (Tab)GUILayout.Toolbar((int)_tab, new[] { "Google Drive", "GitHub / UPM" });
                _search = SearchField(_search);
                EditorGUILayout.Space();

                _scroll = EditorGUILayout.BeginScrollView(_scroll);
                if (_tab == Tab.Drive) DrawDriveTab();
                else DrawGitHubTab();
                EditorGUILayout.EndScrollView();
            }

            DrawFooter();
        }

        void DrawFooter()
        {
            if (_busy) ProgressBarLine();
            else if (!string.IsNullOrEmpty(_status))
                EditorGUILayout.HelpBox(_status, MessageType.Info);
        }

        void ProgressBarLine()
        {
            var r = GUILayoutUtility.GetRect(0, 22, GUILayout.ExpandWidth(true));
            float v = _progTotal > 0
                ? (float)_progDone / _progTotal
                : (float)(EditorApplication.timeSinceStartup % 1.0); // indeterminate sweep
            EditorGUI.ProgressBar(r, v, string.IsNullOrEmpty(_status) ? "Working..." : _status);
        }

        // ---- credential file attach page -----------------------------------
        void DrawAttachPage()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
                GUILayout.Label("MyUnityHub - Credential File", EditorStyles.boldLabel);

            EditorGUILayout.Space();

            if (_credsError != null)
                EditorGUILayout.HelpBox("Could not read the credential file:\n" + _credsError,
                    MessageType.Error);
            else
                EditorGUILayout.HelpBox(
                    "Credentials are not kept in the editor. They are read from a " +
                    $".{CredentialStore.Extension} file.\n\n" +
                    "• Already have one: Select Credential File\n" +
                    "• Don't have one: New Credential File (type the values, they go to the file)",
                    MessageType.Info);

            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Select Credential File...", GUILayout.Height(28)))
                    PickCredentialFile();
                if (GUILayout.Button("New Credential File...", GUILayout.Height(28)))
                    CredentialFileWizard.Open();
            }

            string path = CredentialStore.FilePath;
            if (!string.IsNullOrEmpty(path))
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Selected file", path, EditorStyles.wordWrappedMiniLabel);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Reload")) ReloadCredentialsFromUI();
                    if (GUILayout.Button("Detach")) DetachCredentialFile();
                }
            }

            DrawLegacyWarning();
            EditorGUILayout.Space();
            DrawFooter();
        }

        /// <summary>Older versions kept secrets in EditorPrefs. Keep nagging until they
        /// are out of there, whichever page the user is on.</summary>
        void DrawLegacyWarning()
        {
            if (!CredentialStore.HasLegacyPrefs) return;
            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(
                "Credentials from an older version are still in Unity's settings " +
                "(EditorPrefs). Move them into a file, or delete them.", MessageType.Warning);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Move to File and Erase")) MigrateLegacy();
                if (GUILayout.Button("Erase Only")) ClearLegacy();
            }
        }

        void ClearLegacy()
        {
            if (!EditorUtility.DisplayDialog("Erase old credentials",
                    "The old Client ID / Secret / PAT / refresh token will be permanently " +
                    "deleted from Unity's settings. Continue?", "Erase", "Cancel"))
                return;
            CredentialStore.ClearLegacyPrefs();
            _status = "Old credentials erased from Unity's settings.";
            GUIUtility.ExitGUI();
        }

        // Every one of these can flip which page OnGUI should be drawing, so they end
        // with ExitGUI: continuing the current pass would mismatch Layout and Repaint.
        // ExitGUI throws, so it must stay outside the try blocks below.

        void PickCredentialFile()
        {
            string p = EditorUtility.OpenFilePanel("Select Credential File", "", CredentialStore.Extension);
            if (string.IsNullOrEmpty(p)) return;
            CredentialStore.FilePath = p;
            ReloadCredentials();
            if (_creds != null) _status = "Credential file loaded.";
            GUIUtility.ExitGUI();
        }

        void MigrateLegacy()
        {
            string p = EditorUtility.SaveFilePanel("Save Old Credentials",
                "", "myunityhub." + CredentialStore.Extension, CredentialStore.Extension);
            if (string.IsNullOrEmpty(p)) return;
            try
            {
                CredentialStore.MigrateLegacyPrefsTo(p);
                ReloadCredentials();
                _status = "Old credentials moved to the file and erased from Unity's settings.";
            }
            catch (System.Exception e) { _credsError = e.Message; }
            GUIUtility.ExitGUI();
        }

        void DetachCredentialFile()
        {
            CredentialStore.Detach();
            ReloadCredentials();
            GUIUtility.ExitGUI();
        }

        void ReloadCredentialsFromUI()
        {
            ReloadCredentials();
            GUIUtility.ExitGUI();
        }

        // ---- settings page -------------------------------------------------
        void DrawSettingsPage()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                using (new EditorGUI.DisabledScope(_busy))
                    if (GUILayout.Button("← Back", EditorStyles.toolbarButton, GUILayout.Width(70)))
                        _view = View.Main;
                GUILayout.Label("Settings / Credentials", EditorStyles.boldLabel);
            }
            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(_busy))
                DrawSettingsBody();
            EditorGUILayout.Space();
            DrawFooter();
        }

        void DrawSettingsBody()
        {
            string path = CredentialStore.FilePath;

            EditorGUILayout.LabelField("Credential file", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(path, EditorStyles.wordWrappedMiniLabel);

            if (CredentialStore.IsInsideProject(path))
                EditorGUILayout.HelpBox(
                    "This file is inside the Unity project. Move it outside so it cannot " +
                    "be committed by accident.", MessageType.Warning);

            // Values are shown as present/absent only - the point of the file is that
            // secrets never appear in the editor.
            EditorGUILayout.LabelField("Google Client ID / Secret", Mark(_creds.HasGoogle));
            EditorGUILayout.LabelField("Drive Root Folder ID", Mark(_creds.RootFolderId.Length > 0));
            EditorGUILayout.LabelField("GitHub PAT", Mark(_creds.HasGithub));

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Reload")) ReloadCredentialsFromUI();
                if (GUILayout.Button("Reveal")) EditorUtility.RevealInFinder(path);
                if (GUILayout.Button("Select Other...")) PickCredentialFile();
                if (GUILayout.Button("Detach")) DetachCredentialFile();
            }

            EditorGUILayout.Space();
            bool loggedIn = Drive.HasSavedLogin;
            if (loggedIn) DrawGreenBadge("● Google Drive: signed in");

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(!_creds.HasGoogle))
                    if (GUILayout.Button("Sign in with Google")) _ = DoLogin();
                if (loggedIn && GUILayout.Button("Sign out"))
                {
                    Drive.SignOut();
                    ReloadCredentials(); // the refresh token line is gone from the file
                    _status = "Drive session cleared.";
                }
            }

            DrawLegacyWarning();
        }

        static string Mark(bool set) => set ? "✔ present in file" : "✘ missing from file";

        async Task DoLogin()
        {
            // Same rule as LoadDrive: build the client on the main thread, run the HTTP
            // work off it so the editor stays live.
            var drive = Drive;
            BeginBusy(0);
            try
            {
                _status = "Opening the Google sign-in page in your browser...";
                await Task.Run(() => drive.Login());
                _status = "Google Drive connected.";
            }
            catch (System.Exception e) { _status = "Sign-in failed: " + e.Message; }
            finally { EndBusy(); }
        }

        static void DrawGreenBadge(string text)
        {
            var style = new GUIStyle(EditorStyles.boldLabel)
            {
                normal = { textColor = new Color(0.25f, 0.8f, 0.35f) }
            };
            GUILayout.Label(text, style);
        }

        // ===================================================================
        // DRIVE TAB
        // ===================================================================
        void DrawDriveTab()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Refresh List")) _ = LoadDrive(force: true);
                if (GUILayout.Button("Upload File...")) _ = UploadLocal(folder: false);
                if (GUILayout.Button("Upload Folder...")) _ = UploadLocal(folder: true);
            }

            if (_driveItems == null)
            {
                EditorGUILayout.HelpBox(
                    "Set drive.rootFolderId in the credential file, then hit 'Refresh List'.",
                    MessageType.None);
                return;
            }

            foreach (var f in Filter(_driveItems, f => f.name))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (f.IsFolder)
                    {
                        bool on = _selectedFolderId == f.id;
                        bool now = EditorGUILayout.ToggleLeft("📁 " + f.name, on);
                        if (now != on) _selectedFolderId = now ? f.id : null; // radio
                    }
                    else
                    {
                        bool on = _selectedFiles.Contains(f.id);
                        bool now = EditorGUILayout.ToggleLeft("📄 " + f.name, on);
                        if (now && !on) _selectedFiles.Add(f.id);
                        else if (!now && on) _selectedFiles.Remove(f.id);
                    }
                }
            }

            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(_selectedFiles.Count == 0))
                    if (GUILayout.Button($"Import {_selectedFiles.Count} File(s)")) _ = ImportFiles();
                using (new EditorGUI.DisabledScope(_selectedFolderId == null))
                    if (GUILayout.Button("Import Selected Folder")) _ = ImportFolder();
            }
        }

        async Task LoadDrive(bool force)
        {
            string rootId = _creds.RootFolderId;
            if (string.IsNullOrEmpty(rootId))
            {
                _status = "drive.rootFolderId is empty in the credential file.";
                SafeRepaint();
                return;
            }
            BeginBusy(0);
            try
            {
                _status = "Listing Drive...";
                if (force || !_driveCache.TryGet(rootId, out _driveItems))
                {
                    // Materialize the client on the main thread, then Task.Run:
                    // HttpClient's sync prelude (proxy/DNS) must stay off the main
                    // thread or the editor freezes.
                    var drive = Drive;
                    _driveItems = await Task.Run(() => drive.ListFolder(rootId));
                    _driveCache.Set(rootId, _driveItems);
                }
                SaveDriveItems(); // persist across editor restarts
                _status = $"{_driveItems.Count} item(s).";
            }
            catch (System.Exception e) { _status = "Drive error: " + e.Message; }
            finally { EndBusy(); }
        }

        async Task ImportFiles()
        {
            string dir = AskTargetDir();
            if (dir == null) return;
            var picks = _driveItems.Where(f => _selectedFiles.Contains(f.id)).ToList();

            // conflict pass (main thread, before any network)
            var jobs = new List<(DriveFile f, string path)>();
            foreach (var f in picks)
            {
                string path = Path.Combine(dir, f.name);
                if (File.Exists(path))
                {
                    int c = EditorUtility.DisplayDialogComplex("Conflict",
                        $"'{f.name}' already exists.", "Overwrite", "Cancel", "Skip");
                    if (c == 1) return;     // Cancel whole batch
                    if (c == 2) continue;   // Skip
                }
                jobs.Add((f, path));
            }
            if (jobs.Count == 0) { _status = "Nothing to download."; SafeRepaint(); return; }

            // batch: freeze compilation until all files land, then one import.
            var drive = Drive;
            BeginBusy(jobs.Count);
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var (f, path) in jobs)
                {
                    _status = $"Downloading {_progDone + 1}/{jobs.Count}: {f.name}";
                    await Task.Run(() => drive.DownloadFile(f.id, path));
                    _progDone++;
                }
                _status = $"{jobs.Count} file(s) imported.";
                _selectedFiles.Clear();
            }
            catch (System.Exception e) { _status = "Download error: " + e.Message; }
            finally
            {
                AssetDatabase.StopAssetEditing();
                AssetDatabase.Refresh();
                EndBusy();
            }
        }

        async Task ImportFolder()
        {
            string dir = AskTargetDir();
            if (dir == null) return;
            var folder = _driveItems.First(f => f.id == _selectedFolderId);
            string dest = Path.Combine(dir, folder.name);
            if (Directory.Exists(dest))
            {
                int c = EditorUtility.DisplayDialogComplex("Conflict",
                    $"Folder '{folder.name}' already exists.\n\n" +
                    "Overwrite: the existing folder is DELETED and downloaded again.",
                    "Overwrite", "Cancel", "Skip");
                if (c != 0) { _status = "Cancelled/skipped."; return; } // only overwrite proceeds
                // A plain re-download would leave files that no longer exist on Drive.
                // DeleteAsset (not Directory.Delete) so the .meta files go too.
                if (!AssetDatabase.DeleteAsset(ToAssetPath(dest)))
                {
                    _status = "Could not delete the existing folder: " + dest;
                    return;
                }
            }

            var drive = Drive;
            BeginBusy(0);
            AssetDatabase.StartAssetEditing();
            try
            {
                _status = $"Downloading folder: {folder.name}";
                await Task.Run(() => drive.DownloadFolder(folder, dir));
                _status = $"Folder imported: {folder.name}";
            }
            catch (System.Exception e) { _status = "Download error: " + e.Message; }
            finally
            {
                AssetDatabase.StopAssetEditing();
                AssetDatabase.Refresh();
                EndBusy();
            }
        }

        async Task UploadLocal(bool folder)
        {
            string rootId = _creds.RootFolderId;
            if (string.IsNullOrEmpty(rootId))
            {
                _status = "drive.rootFolderId is empty in the credential file (upload target).";
                return;
            }
            string path = folder
                ? EditorUtility.OpenFolderPanel("Upload Folder", Application.dataPath, "")
                : EditorUtility.OpenFilePanel("Upload File", Application.dataPath, "");
            if (string.IsNullOrEmpty(path)) return;
            var drive = Drive;
            BeginBusy(0);
            try
            {
                _status = folder ? "Uploading folder..." : "Uploading file...";
                if (folder) await Task.Run(() => drive.UploadFolder(path, rootId));
                else await Task.Run(() => drive.UploadFile(path, rootId));
                _status = "Uploaded.";
                _driveCache.Clear();
            }
            catch (System.Exception e) { _status = "Upload error: " + e.Message; }
            finally { EndBusy(); }
        }

        // ===================================================================
        // GITHUB TAB
        // ===================================================================
        void DrawGitHubTab()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Refresh Repos")) _ = LoadRepos(force: true);
            }
            if (_repos == null)
            {
                EditorGUILayout.HelpBox(
                    "Set github.pat in the credential file, then hit 'Refresh Repos'.",
                    MessageType.None);
                return;
            }

            foreach (var r in Filter(_repos.Where(r => r.isPackage), r => r.name))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField("📦 " + r.full_name);
                    if (GUILayout.Button("Add UPM", GUILayout.Width(90)))
                        AddUpm(r);
                }
            }
        }

        async Task LoadRepos(bool force)
        {
            if (!_creds.HasGithub)
            {
                _status = "github.pat is empty in the credential file.";
                SafeRepaint();
                return;
            }
            BeginBusy(0);
            try
            {
                _status = "Listing repos...";
                if (force || !_repoCache.TryGet("repos", out _repos))
                {
                    var gh = GitHub;
                    var repos = await Task.Run(() => gh.ListRepos());
                    await Task.Run(() => gh.ProbeIsPackages(repos)); // throttled to 8 in flight
                    // atomic swap: isPackage is mutated above; assigning only when fully
                    // populated keeps the drawn list stable between Layout/Repaint passes.
                    _repos = repos;
                    _repoCache.Set("repos", _repos);
                    SaveRepos(); // persist across editor restarts
                }
                _status = $"{_repos.Count(r => r.isPackage)} UPM package(s).";
            }
            catch (System.Exception e) { _status = "GitHub error: " + e.Message; }
            finally { EndBusy(); }
        }

        void AddUpm(Repo r)
        {
            _status = $"Adding UPM package: {r.name}";
            SafeRepaint();
            UpmInstaller.Add(r.GitUrl, (ok, msg) =>
            {
                _status = ok ? $"Added: {msg}" : $"UPM error: {msg}";
                SafeRepaint();
            });
        }

        // ===================================================================
        // helpers
        // ===================================================================
        static string Norm(string p) => p.Replace('\\', '/').TrimEnd('/');

        /// <summary>"C:/proj/Assets/Foo" -> "Assets/Foo" (caller guarantees it is under Assets).</summary>
        static string ToAssetPath(string absolute) =>
            "Assets" + Norm(absolute).Substring(Norm(Application.dataPath).Length);

        string AskTargetDir()
        {
            string dir = EditorUtility.OpenFolderPanel("Target Folder (inside Assets)", Application.dataPath, "");
            if (string.IsNullOrEmpty(dir)) return null;
            // Trailing separator matters: without it a sibling like ".../AssetsBackup"
            // passes a bare StartsWith against ".../Assets".
            string root = Norm(Application.dataPath);
            string norm = Norm(dir);
            if (norm != root && !norm.StartsWith(root + "/"))
            {
                _status = "The target must be inside the Assets folder.";
                return null;
            }
            return dir;
        }

        IEnumerable<T> Filter<T>(IEnumerable<T> src, System.Func<T, string> name)
        {
            if (string.IsNullOrEmpty(_search)) return src;
            return src.Where(x => name(x).IndexOf(_search, System.StringComparison.OrdinalIgnoreCase) >= 0);
        }

        static string SearchField(string val)
        {
            // ponytail: ToolbarSearchField via reflection is fragile across versions;
            // a plain text field filters just as well.
            return EditorGUILayout.TextField("Search", val);
        }
    }
}


