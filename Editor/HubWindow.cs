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
        // ---- persisted settings (EditorPrefs) ------------------------------
        const string K_ClientId = "DriveGitHubHub.clientId";
        const string K_Secret   = "DriveGitHubHub.clientSecret";
        const string K_RootId   = "DriveGitHubHub.rootFolderId";
        const string K_Pat      = "DriveGitHubHub.githubPat";
        const string K_DriveItems = "DriveGitHubHub.driveItems";
        const string K_Repos      = "DriveGitHubHub.repos";

        // fetched lists are project-specific (rootId differs per project)
        static string PKey(string k) => k + "_" + Application.dataPath.GetHashCode();

        string _clientId, _secret, _rootId, _pat;

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

        [MenuItem("Tools/MyUnityHub")]
        public static void Open() => GetWindow<HubWindow>("MyUnityHub");

        void OnEnable()
        {
            _clientId = EditorPrefs.GetString(K_ClientId, "");
            _secret   = EditorPrefs.GetString(K_Secret, "");
            _rootId   = EditorPrefs.GetString(K_RootId, "");
            _pat      = EditorPrefs.GetString(K_Pat, "");
            // open settings page first run if creds missing
            _view = (string.IsNullOrEmpty(_clientId) || string.IsNullOrEmpty(_pat)) ? View.Settings : View.Main;
            LoadCachedLists();
        }

        // ---- persistent list cache (survives editor restart / domain reload) ----
        void LoadCachedLists()
        {
            var d = EditorPrefs.GetString(PKey(K_DriveItems), "");
            if (!string.IsNullOrEmpty(d))
            {
                var w = JsonUtility.FromJson<DriveFileList>(d);
                if (w?.files != null) _driveItems = w.files.ToList();
            }
            var r = EditorPrefs.GetString(PKey(K_Repos), "");
            if (!string.IsNullOrEmpty(r))
            {
                var w = JsonUtility.FromJson<RepoArrayWrap>(r);
                if (w?.items != null) _repos = w.items.ToList();
            }
        }

        void SaveDriveItems() =>
            EditorPrefs.SetString(PKey(K_DriveItems),
                JsonUtility.ToJson(new DriveFileList { files = _driveItems.ToArray() }));

        void SaveRepos() =>
            EditorPrefs.SetString(PKey(K_Repos),
                JsonUtility.ToJson(new RepoArrayWrap { items = _repos.ToArray() }));

        void OnDisable()
        {
            if (_busy) EditorApplication.update -= Repaint; // drop the animation pump
        }

        // ---- busy / progress ----------------------------------------------
        // total>0 => real fraction; total==0 => indeterminate, animated each frame.
        void BeginBusy(int total)
        {
            _busy = true; _progTotal = total; _progDone = 0;
            EditorApplication.update += Repaint; // pump repaints so the bar animates
            Repaint();
        }

        void EndBusy()
        {
            _busy = false; _progTotal = 0; _progDone = 0;
            EditorApplication.update -= Repaint;
            Repaint();
        }

        GoogleDriveClient Drive => _drive ??= new GoogleDriveClient(_clientId, _secret);
        GitHubClient GitHub => _github ??= new GitHubClient(_pat);

        // ===================================================================
        void OnGUI()
        {
            if (_view == View.Settings) { DrawSettingsPage(); return; }

            // top bar
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                GUILayout.Label("MyUnityHub", EditorStyles.boldLabel);
                GUILayout.FlexibleSpace();
                if (Drive.HasSavedLogin) DrawGreenBadge("● Drive bağlı");
                using (new EditorGUI.DisabledScope(_busy))
                    if (GUILayout.Button("⚙ Ayarlar", EditorStyles.toolbarButton, GUILayout.Width(90)))
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
            EditorGUI.ProgressBar(r, v, string.IsNullOrEmpty(_status) ? "İşlem sürüyor..." : _status);
        }

        // ---- settings page -------------------------------------------------
        void DrawSettingsPage()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                using (new EditorGUI.DisabledScope(_busy))
                    if (GUILayout.Button("← Geri", EditorStyles.toolbarButton, GUILayout.Width(70)))
                        _view = View.Main;
                GUILayout.Label("Ayarlar / Kimlik Bilgileri", EditorStyles.boldLabel);
            }
            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(_busy))
                DrawSettingsBody();
            if (_busy) ProgressBarLine(); // only operation progress here, no idle status
        }

        void DrawSettingsBody()
        {
            bool loggedIn = Drive.HasSavedLogin;
            if (loggedIn) DrawGreenBadge("● Google Drive: Giriş yapıldı");

            // all fields always visible, masked as *
            EditorGUI.BeginChangeCheck();
            _clientId = EditorGUILayout.PasswordField("Google Client ID", _clientId);
            _secret   = EditorGUILayout.PasswordField("Google Client Secret", _secret);
            _rootId   = EditorGUILayout.PasswordField("Drive Root Folder ID", _rootId);
            _pat      = EditorGUILayout.PasswordField("GitHub PAT", _pat);
            if (EditorGUI.EndChangeCheck())
            {
                EditorPrefs.SetString(K_ClientId, _clientId);
                EditorPrefs.SetString(K_Secret, _secret);
                EditorPrefs.SetString(K_RootId, _rootId);
                EditorPrefs.SetString(K_Pat, _pat);
                _drive = null; _github = null; // rebuild clients with new creds
            }

            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                GUI.enabled = !_busy && !string.IsNullOrEmpty(_clientId) && !string.IsNullOrEmpty(_secret);
                if (GUILayout.Button("Google ile Giriş Yap")) _ = DoLogin();
                GUI.enabled = !_busy;
                if (loggedIn && GUILayout.Button("Çıkış Yap"))
                {
                    Drive.SignOut();
                    _status = "Drive oturumu temizlendi.";
                }
            }
        }

        async Task DoLogin()
        {
            BeginBusy(0);
            try
            {
                _status = "Google girişi açılıyor (tarayıcı)...";
                await Drive.Login();
                _status = "Google Drive bağlandı.";
            }
            catch (System.Exception e) { _status = "Giriş hata: " + e.Message; }
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
                EditorGUILayout.HelpBox("Root Folder ID gir ve 'Refresh List' bas.", MessageType.None);
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
                GUI.enabled = !_busy && _selectedFiles.Count > 0;
                if (GUILayout.Button($"Import {_selectedFiles.Count} File(s)")) _ = ImportFiles();
                GUI.enabled = !_busy && _selectedFolderId != null;
                if (GUILayout.Button("Import Selected Folder")) _ = ImportFolder();
                GUI.enabled = !_busy;
            }
        }

        async Task LoadDrive(bool force)
        {
            if (string.IsNullOrEmpty(_rootId)) { _status = "Root Folder ID bos."; Repaint(); return; }
            BeginBusy(0);
            try
            {
                _status = "Drive listeleniyor...";
                if (force || !_driveCache.TryGet(_rootId, out _driveItems))
                {
                    // Materialize client on main thread (ctor reads EditorPrefs), then
                    // Task.Run: HttpClient's sync prelude (proxy/DNS) must stay off the
                    // main thread or the editor freezes.
                    var drive = Drive;
                    _driveItems = await Task.Run(() => drive.ListFolder(_rootId));
                    _driveCache.Set(_rootId, _driveItems);
                }
                SaveDriveItems(); // persist across editor restarts
                _status = $"{_driveItems.Count} oge.";
            }
            catch (System.Exception e) { _status = "Drive hata: " + e.Message; }
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
                        $"'{f.name}' zaten var.", "Overwrite", "Cancel", "Skip");
                    if (c == 1) return;     // Cancel whole batch
                    if (c == 2) continue;   // Skip
                }
                jobs.Add((f, path));
            }
            if (jobs.Count == 0) { _status = "Indirilecek dosya yok."; Repaint(); return; }

            // batch: freeze compilation until all files land, then one import.
            var drive = Drive;
            BeginBusy(jobs.Count);
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var (f, path) in jobs)
                {
                    _status = $"İndiriliyor {_progDone + 1}/{jobs.Count}: {f.name}";
                    await Task.Run(() => drive.DownloadFile(f.id, path));
                    _progDone++;
                }
                _status = $"{jobs.Count} dosya alındı.";
                _selectedFiles.Clear();
            }
            catch (System.Exception e) { _status = "Indirme hata: " + e.Message; }
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
                    $"'{folder.name}' klasoru zaten var.", "Overwrite", "Cancel", "Skip");
                if (c != 0) { _status = "Iptal/atlandi."; return; } // only overwrite proceeds
            }

            var drive = Drive;
            BeginBusy(0);
            AssetDatabase.StartAssetEditing();
            try
            {
                _status = $"Klasör indiriliyor: {folder.name}";
                await Task.Run(() => drive.DownloadFolder(folder, dir));
                _status = $"Klasör alındı: {folder.name}";
            }
            catch (System.Exception e) { _status = "Indirme hata: " + e.Message; }
            finally
            {
                AssetDatabase.StopAssetEditing();
                AssetDatabase.Refresh();
                EndBusy();
            }
        }

        async Task UploadLocal(bool folder)
        {
            if (string.IsNullOrEmpty(_rootId)) { _status = "Root Folder ID bos (upload hedefi)."; return; }
            string path = folder
                ? EditorUtility.OpenFolderPanel("Upload Folder", Application.dataPath, "")
                : EditorUtility.OpenFilePanel("Upload File", Application.dataPath, "");
            if (string.IsNullOrEmpty(path)) return;
            var drive = Drive;
            BeginBusy(0);
            try
            {
                _status = folder ? "Klasör yükleniyor..." : "Dosya yükleniyor...";
                if (folder) await Task.Run(() => drive.UploadFolder(path, _rootId));
                else await Task.Run(() => drive.UploadFile(path, _rootId));
                _status = "Yüklendi.";
                _driveCache.Clear();
            }
            catch (System.Exception e) { _status = "Upload hata: " + e.Message; }
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
                EditorGUILayout.HelpBox("PAT gir ve 'Refresh Repos' bas.", MessageType.None);
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
            BeginBusy(0);
            try
            {
                _status = "Repolar listeleniyor...";
                if (force || !_repoCache.TryGet("repos", out _repos))
                {
                    var gh = GitHub;
                    var repos = await Task.Run(() => gh.ListRepos());
                    // probe package.json in parallel (capped naturally by repo count)
                    await Task.Run(() => Task.WhenAll(repos.Select(gh.ProbeIsPackage)));
                    // atomic swap: isPackage is mutated above; assigning only when fully
                    // populated keeps the drawn list stable between Layout/Repaint passes.
                    _repos = repos;
                    _repoCache.Set("repos", _repos);
                    SaveRepos(); // persist across editor restarts
                }
                _status = $"{_repos.Count(r => r.isPackage)} UPM paketi.";
            }
            catch (System.Exception e) { _status = "GitHub hata: " + e.Message; }
            finally { EndBusy(); }
        }

        void AddUpm(Repo r)
        {
            _status = $"UPM ekleniyor: {r.name}";
            Repaint();
            UpmInstaller.Add(r.GitUrl, (ok, msg) =>
            {
                _status = ok ? $"Eklendi: {msg}" : $"UPM hata: {msg}";
                Repaint();
            });
        }

        // ===================================================================
        // helpers
        // ===================================================================
        string AskTargetDir()
        {
            string dir = EditorUtility.OpenFolderPanel("Hedef Dizin (Assets icinde)", Application.dataPath, "");
            if (string.IsNullOrEmpty(dir)) return null;
            if (!dir.Replace('\\', '/').StartsWith(Application.dataPath.Replace('\\', '/')))
            {
                _status = "Hedef Assets klasoru icinde olmali.";
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


