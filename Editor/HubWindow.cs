using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace MyUnityHub
{
    /// <summary>One row of the Drive tree. Lives in memory only; the window persists the
    /// flat <see cref="DriveTreeCache"/> instead, because JsonUtility cannot serialize a
    /// recursive type.</summary>
    internal class DriveNode
    {
        public DriveFile file;
        public DriveNode parent;         // null on the root; walked up for paths and ticks
        public List<DriveNode> children; // null => never listed, empty => listed and empty
        public bool expanded;
        public bool loading;
    }

    /// <summary>The Drive tree flattened for Library/. Each file carries its parents, and
    /// <see cref="known"/> records which folders were listed, so an empty folder comes back
    /// as empty rather than as unexplored.</summary>
    [Serializable]
    internal class DriveTreeCache
    {
        public string rootId;
        public DriveFile[] files;
        public string[] known;
    }

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
        bool _reloadLocked;         // assembly reloads held off while an op runs
        int _progDone, _progTotal;  // _progTotal==0 => indeterminate (animated bar)

        GoogleDriveClient _drive;
        GitHubClient _github;

        readonly SimpleCache<List<Repo>> _repoCache = new SimpleCache<List<Repo>>(120);

        // Synthetic node standing for drive.rootFolderId; its children are the top level.
        DriveNode _root;
        // One tick set for files and folders alike. Ticking a folder ticks everything under
        // it; unticking anything inside clears the folder's own tick, which leaves the
        // folder "partial" - it still holds a selection, it just is not all of it.
        readonly HashSet<string> _selected = new HashSet<string>();
        readonly HashSet<string> _partial = new HashSet<string>(); // drawn as a dash

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
            LoadCachedTree();
            var r = ReadCache("repos");
            if (!string.IsNullOrEmpty(r))
            {
                var w = JsonUtility.FromJson<RepoArrayWrap>(r);
                if (w?.items != null) _repos = w.items.ToList();
            }
        }

        void SaveTree()
        {
            if (_root == null) return;
            var files = new List<DriveFile>();
            var known = new List<string>();
            foreach (var n in AllNodes())
            {
                if (n != _root) files.Add(n.file);
                if (n.children != null) known.Add(n.file.id);
            }
            WriteCache("driveTree", JsonUtility.ToJson(new DriveTreeCache
            {
                rootId = _root.file.id,
                files = files.ToArray(),
                known = known.ToArray(),
            }));
        }

        void LoadCachedTree()
        {
            var json = ReadCache("driveTree");
            if (string.IsNullOrEmpty(json)) return;
            var c = JsonUtility.FromJson<DriveTreeCache>(json);
            if (c == null || c.files == null || string.IsNullOrEmpty(c.rootId)) return;

            var root = new DriveNode
            {
                file = new DriveFile { id = c.rootId, mimeType = DriveFile.FolderMimeType },
            };
            var byId = new Dictionary<string, DriveNode> { [c.rootId] = root };
            foreach (var file in c.files) byId[file.id] = new DriveNode { file = file };
            foreach (var id in c.known ?? new string[0])
                if (byId.TryGetValue(id, out var n)) n.children = new List<DriveNode>();

            // Re-hang each file under the parent it was listed from. A parent that was
            // never listed has no children list, so nothing is dropped in the wrong place.
            foreach (var file in c.files)
                foreach (var p in file.parents ?? new string[0])
                    if (byId.TryGetValue(p, out var parent) && parent.children != null)
                    {
                        byId[file.id].parent = parent;
                        parent.children.Add(byId[file.id]);
                        break;
                    }
            _root = root;
        }

        void SaveRepos() =>
            WriteCache("repos", JsonUtility.ToJson(new RepoArrayWrap { items = _repos.ToArray() }));

        void OnDisable()
        {
            EditorApplication.update -= SafeRepaint; // drop the animation pump
            UnlockReloads();                         // never leave the lock behind
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
            // A domain reload mid-operation kills the continuation and can strand
            // AssetDatabase.StartAssetEditing, which leaves the whole project locked.
            if (!_reloadLocked)
            {
                EditorApplication.LockReloadAssemblies();
                _reloadLocked = true;
            }
            EditorApplication.update += SafeRepaint; // pump repaints so the bar animates
            SafeRepaint();
        }

        void EndBusy()
        {
            _busy = false; _progTotal = 0; _progDone = 0;
            UnlockReloads();
            EditorApplication.update -= SafeRepaint;
            SafeRepaint();
        }

        void UnlockReloads()
        {
            if (!_reloadLocked) return;
            _reloadLocked = false;
            EditorApplication.UnlockReloadAssemblies();
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
                    if (GUILayout.Button("Sign in with Google")) Defer(DoLogin);
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
                if (GUILayout.Button("Refresh List")) Defer(() => LoadDrive(force: true));
                if (GUILayout.Button("Upload File...")) Defer(() => UploadLocal(folder: false));
                if (GUILayout.Button("Upload Folder...")) Defer(() => UploadLocal(folder: true));
            }

            if (_root?.children == null)
            {
                EditorGUILayout.HelpBox(
                    "Set drive.rootFolderId in the credential file, then hit 'Refresh List'.",
                    MessageType.None);
                return;
            }

            if (string.IsNullOrEmpty(_search))
                foreach (var n in _root.children) DrawNode(n, 0);
            else
                // Search flattens the tree: a hit four folders deep is easier to act on as
                // one row than as a path the user has to open by hand. Only what has been
                // listed so far is searchable - unopened folders were never fetched.
                foreach (var n in AllNodes())
                    if (n != _root && Matches(n.file.name)) DrawRow(n, 0, arrow: false);

            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(_selected.Count == 0))
                if (GUILayout.Button($"Import {_selected.Count} Selected Item(s)"))
                    Defer(ImportSelected);
        }

        // ---- tree ----------------------------------------------------------

        const float IndentStep = 14f;
        const float ArrowWidth = 13f;

        void DrawNode(DriveNode n, int depth)
        {
            DrawRow(n, depth, arrow: true);
            if (n.expanded && n.children != null)
                foreach (var c in n.children) DrawNode(c, depth + 1);
        }

        void DrawRow(DriveNode n, int depth, bool arrow)
        {
            var file = n.file;
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(depth * IndentStep);

                // The arrow slot is reserved for every row so names stay aligned, but the
                // triangle is only drawn for a folder that actually has something in it.
                var slot = GUILayoutUtility.GetRect(ArrowWidth, ArrowWidth, GUILayout.Width(ArrowWidth));
                if (arrow && CanExpand(n))
                {
                    bool open = EditorGUI.Foldout(slot, n.expanded, GUIContent.none, true);
                    if (open != n.expanded)
                    {
                        n.expanded = open;
                        if (open && NeedsReveal(n)) Defer(() => Reveal(n));
                    }
                }

                string label = (file.IsFolder ? "📁 " : "📄 ") + file.name +
                               (n.loading ? "  ..." : "");
                bool on = _selected.Contains(file.id);

                // A dash means "something inside is picked, but not the whole folder".
                // Clicking it takes the whole folder, which is what the dash negates.
                EditorGUI.showMixedValue = !on && _partial.Contains(file.id);
                bool now = EditorGUILayout.ToggleLeft(label, on);
                EditorGUI.showMixedValue = false;
                if (now != on) Select(n, now);
            }
        }

        /// <summary>A folder that was listed and came back empty gets no arrow; one that
        /// has never been listed keeps its arrow, so a partial cache stays openable.</summary>
        static bool CanExpand(DriveNode n) =>
            n.file.IsFolder && (n.children == null || n.children.Count > 0);

        static bool NeedsReveal(DriveNode n) =>
            n.children == null || n.children.Any(c => c.file.IsFolder && c.children == null);

        // ---- selection -----------------------------------------------------

        /// <summary>Tick or untick a row and everything under it. Unticking also clears
        /// every folder above, because a folder that is missing one child is no longer the
        /// whole folder - and the import walks the ticked folders recursively.</summary>
        void Select(DriveNode n, bool on)
        {
            Cascade(n, on);
            if (!on)
                for (var p = n.parent; p != null; p = p.parent) _selected.Remove(p.file.id);
            RefreshPartial();
        }

        void Cascade(DriveNode n, bool on)
        {
            if (on) _selected.Add(n.file.id);
            else _selected.Remove(n.file.id);
            if (n.children != null)
                foreach (var c in n.children) Cascade(c, on);
        }

        /// <summary>Hand a ticked folder's tick down to rows that only just arrived, so
        /// opening a ticked folder never shows unticked children inside it.</summary>
        void PropagateSelection(DriveNode from)
        {
            if (from.children == null) return;
            bool on = _selected.Contains(from.file.id);
            foreach (var c in from.children)
            {
                if (on) _selected.Add(c.file.id);
                PropagateSelection(c);
            }
        }

        /// <summary>Recomputed on change rather than per repaint: which folders hold a
        /// selection without being selected themselves.</summary>
        void RefreshPartial()
        {
            _partial.Clear();
            foreach (var n in AllNodes())
            {
                if (!_selected.Contains(n.file.id)) continue;
                // stops before the synthetic root: it is never drawn, so it can never
                // be the folder wearing the dash
                for (var p = n.parent; p != null && p.parent != null; p = p.parent)
                    if (!_selected.Contains(p.file.id)) _partial.Add(p.file.id);
            }
        }

        /// <summary>The topmost ticked rows. Anything below one of them is already covered:
        /// a ticked folder is downloaded recursively, including the parts never listed.</summary>
        IEnumerable<DriveNode> SelectionRoots() =>
            AllNodes().Where(n => n != _root && _selected.Contains(n.file.id) &&
                                  (n.parent == null || !_selected.Contains(n.parent.file.id)));

        /// <summary>Where this row sits relative to the Drive root, excluding its own name.
        /// Rebuilding that path under the target keeps a partial selection in shape and
        /// keeps two same-named files from different folders apart.</summary>
        static string RelDir(DriveNode n)
        {
            var parts = new List<string>();
            for (var p = n.parent; p != null && p.parent != null; p = p.parent)
                parts.Add(p.file.name);
            parts.Reverse();
            return parts.Count == 0 ? "" : Path.Combine(parts.ToArray());
        }

        IEnumerable<DriveNode> AllNodes()
        {
            if (_root == null) yield break;
            var stack = new Stack<DriveNode>();
            stack.Push(_root);
            while (stack.Count > 0)
            {
                var n = stack.Pop();
                yield return n;
                // pushed back-to-front so the walk comes out in the order they are drawn
                if (n.children != null)
                    for (int i = n.children.Count - 1; i >= 0; i--) stack.Push(n.children[i]);
            }
        }

        /// <summary>
        /// Fill in what the tree does not know under this node: its own children if it was
        /// never listed, plus the children of each subfolder one level down so their arrows
        /// are right the moment they appear. Listing is metadata only - nothing is
        /// downloaded until the user asks for an import.
        /// </summary>
        async Task<bool> Reveal(DriveNode n)
        {
            if (n.loading) return false;
            n.loading = true;
            SafeRepaint();
            try
            {
                // Materialize the client on the main thread, then Task.Run: HttpClient's
                // sync prelude (proxy/DNS) must stay off the main thread or the editor
                // freezes.
                var drive = Drive;
                if (n.children == null)
                {
                    var listed = await Task.Run(() => drive.ListFolder(n.file.id));
                    n.children = listed.Select(x => new DriveNode { file = x, parent = n }).ToList();
                }

                var need = n.children.Where(c => c.file.IsFolder && c.children == null)
                                     .Select(c => c.file.id).ToList();
                if (need.Count > 0)
                {
                    var map = await Task.Run(() => drive.ListChildren(need));
                    foreach (var c in n.children)
                        if (c.file.IsFolder && map.TryGetValue(c.file.id, out var kids))
                            c.children = kids.Select(x => new DriveNode { file = x, parent = c }).ToList();
                }

                PropagateSelection(n);
                RefreshPartial();
                SaveTree(); // persist across editor restarts
                return true;
            }
            catch (Exception e)
            {
                _status = "Drive error: " + e.Message;
                return false;
            }
            finally
            {
                n.loading = false;
                SafeRepaint();
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
            if (force || _root == null || _root.file.id != rootId)
            {
                // A rebuilt tree invalidates every id the user had ticked.
                _selected.Clear();
                _partial.Clear();
                _root = new DriveNode
                {
                    file = new DriveFile { id = rootId, mimeType = DriveFile.FolderMimeType },
                };
            }
            BeginBusy(0);
            try
            {
                _status = "Listing Drive...";
                if (await Reveal(_root)) _status = $"{_root.children.Count} item(s) at the top level.";
            }
            finally { EndBusy(); }
        }

        /// <summary>
        /// Download every ticked row into the target folder, rebuilt in the shape it has on
        /// Drive. Only the topmost ticked rows are walked: a ticked folder is fetched
        /// recursively by the client, which also picks up the parts that were never listed
        /// in the window.
        /// </summary>
        async Task ImportSelected()
        {
            string dir = AskTargetDir();
            if (dir == null) return;

            var roots = SelectionRoots().ToList();
            if (roots.Count == 0) { _status = "Nothing selected."; SafeRepaint(); return; }

            // conflict pass (main thread, before any network)
            var jobs = new List<(DriveFile file, string parentDir, string dest)>();
            foreach (var n in roots)
            {
                string parentDir = Path.Combine(dir, RelDir(n));
                string dest = Path.Combine(parentDir, n.file.name);
                bool folder = n.file.IsFolder;
                if (folder ? Directory.Exists(dest) : File.Exists(dest))
                {
                    int c = EditorUtility.DisplayDialogComplex("Conflict",
                        folder
                            ? $"Folder '{n.file.name}' already exists.\n\n" +
                              "Overwrite: the existing folder is DELETED and downloaded again."
                            : $"'{n.file.name}' already exists.",
                        "Overwrite", "Cancel", "Skip");
                    if (c == 1) return;     // Cancel the whole batch
                    if (c == 2) continue;   // Skip
                    // A plain re-download would leave files that no longer exist on Drive.
                    // DeleteAsset (not Directory.Delete) so the .meta files go too.
                    if (folder && !AssetDatabase.DeleteAsset(ToAssetPath(dest)))
                    {
                        _status = "Could not delete the existing folder: " + dest;
                        SafeRepaint();
                        return;
                    }
                }
                jobs.Add((n.file, parentDir, dest));
            }
            if (jobs.Count == 0) { _status = "Nothing to download."; SafeRepaint(); return; }

            // batch: freeze compilation until everything lands, then one import.
            var drive = Drive;
            BeginBusy(jobs.Count);
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var (file, parentDir, dest) in jobs)
                {
                    _status = $"Downloading {_progDone + 1}/{jobs.Count}: {file.name}";
                    SafeRepaint();
                    if (file.IsFolder) await Task.Run(() => drive.DownloadFolder(file, parentDir));
                    else await Task.Run(() => drive.DownloadFile(file.id, dest));
                    _progDone++;
                }
                _status = $"{jobs.Count} item(s) imported.";
                _selected.Clear();
                _partial.Clear();
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
                SafeRepaint();
                return;
            }
            string path = folder
                ? EditorUtility.OpenFolderPanel("Upload Folder", Application.dataPath, "")
                : EditorUtility.OpenFilePanel("Upload File", Application.dataPath, "");
            if (string.IsNullOrEmpty(path)) return;
            var drive = Drive;
            bool uploaded = false;
            BeginBusy(0);
            try
            {
                _status = folder ? "Uploading folder..." : "Uploading file...";
                if (folder) await Task.Run(() => drive.UploadFolder(path, rootId));
                else await Task.Run(() => drive.UploadFile(path, rootId));
                _status = "Uploaded.";
                uploaded = true;
            }
            catch (System.Exception e) { _status = "Upload error: " + e.Message; }
            finally { EndBusy(); }

            // Rebuilt outside the busy block so the two never nest. Costs the open/closed
            // state of the tree, which is the honest price of a list that is now wrong.
            if (uploaded) await LoadDrive(force: true);
        }

        // ===================================================================
        // GITHUB TAB
        // ===================================================================
        void DrawGitHubTab()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Refresh Repos")) Defer(() => LoadRepos(force: true));
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
        // Buttons fire inside OnGUI. Anything that opens a file panel or a modal dialog
        // must not run in the middle of a Layout/Repaint pass, so the operation starts on
        // the next editor tick instead.
        static void Defer(Func<Task> op) => EditorDispatcher.Enqueue(() => _ = op());

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

        IEnumerable<T> Filter<T>(IEnumerable<T> src, Func<T, string> name)
        {
            if (string.IsNullOrEmpty(_search)) return src;
            return src.Where(x => Matches(name(x)));
        }

        bool Matches(string name) =>
            string.IsNullOrEmpty(_search) ||
            name.IndexOf(_search, StringComparison.OrdinalIgnoreCase) >= 0;

        static string SearchField(string val)
        {
            // ponytail: ToolbarSearchField via reflection is fragile across versions;
            // a plain text field filters just as well.
            return EditorGUILayout.TextField("Search", val);
        }
    }
}


