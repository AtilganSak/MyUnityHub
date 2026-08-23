using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace MyUnityHub
{
    /// <summary>
    /// Secrets read out of a .myhub file. Held in memory for as long as the window
    /// lives and rebuilt from disk after every domain reload. Never persisted by the
    /// editor itself.
    /// </summary>
    internal class Credentials
    {
        public string ClientId = "";
        public string ClientSecret = "";
        public string RootFolderId = "";
        public string GithubPat = "";
        public string RefreshToken = "";

        public bool HasGoogle => ClientId.Length > 0 && ClientSecret.Length > 0;
        public bool HasGithub => GithubPat.Length > 0;
    }

    /// <summary>
    /// The .myhub credential file - the only place this tool keeps secrets.
    /// EditorPrefs stores the file *path* and nothing else.
    ///
    /// Format (UTF-8 text):
    ///   MYUNITYHUB/1        first line, magic + format version
    ///   # comment
    ///   key = value         first '=' splits, both sides trimmed
    /// Unknown keys are ignored, so a file written by a newer version still loads.
    /// </summary>
    internal static class CredentialStore
    {
        public const string Extension = "myhub";
        public const string Magic = "MYUNITYHUB/1";

        const string K_Path = "MyUnityHub.credentialFile";

        const string KeyClientId     = "google.clientId";
        const string KeyClientSecret = "google.clientSecret";
        const string KeyRefreshToken = "google.refreshToken";
        const string KeyRootFolderId = "drive.rootFolderId";
        const string KeyGithubPat    = "github.pat";

        // Pre-file-format keys. Listed only so the one-time migration can find and
        // erase them; nothing writes to these any more.
        static readonly string[] LegacyKeys =
        {
            "DriveGitHubHub.clientId", "DriveGitHubHub.clientSecret",
            "DriveGitHubHub.rootFolderId", "DriveGitHubHub.githubPat",
            "DriveGitHubHub.refresh_token",
        };

        // The token refresh runs inside Task.Run, so writes can arrive off the main
        // thread while the UI is reading.
        static readonly object Gate = new object();

        // ---- attachment ----------------------------------------------------

        // EditorPrefs is main-thread only, but Upsert runs from the OAuth refresh on a
        // background task. Read the path once per domain reload on the main thread and
        // serve it from memory afterwards.
        static string _pathCache;

        [InitializeOnLoadMethod]
        static void PrimePathCache() => _pathCache = EditorPrefs.GetString(K_Path, "");

        public static string FilePath
        {
            get => _pathCache ??= EditorPrefs.GetString(K_Path, "");
            set
            {
                _pathCache = value ?? "";
                EditorPrefs.SetString(K_Path, _pathCache);
            }
        }

        public static bool IsAttached => FilePath.Length > 0 && File.Exists(FilePath);

        public static void Detach()
        {
            _pathCache = "";
            EditorPrefs.DeleteKey(K_Path);
        }

        /// <summary>True if the file sits inside the Unity project, i.e. one careless
        /// commit away from being published.</summary>
        public static bool IsInsideProject(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."))
                              .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return Path.GetFullPath(path)
                       .StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        // ---- read ------------------------------------------------------------

        public static Credentials Load()
        {
            string path = FilePath;
            if (string.IsNullOrEmpty(path))
                throw new IOException("No credential file attached.");
            if (!File.Exists(path))
                throw new FileNotFoundException("Credential file not found: " + path, path);

            string[] lines;
            lock (Gate) lines = File.ReadAllLines(path, Encoding.UTF8);
            return Parse(lines, path);
        }

        static Credentials Parse(string[] lines, string path)
        {
            if (lines.Length == 0 || lines[0].Trim() != Magic)
                throw new IOException($"Not a credential file, first line must be '{Magic}': {path}");

            var c = new Credentials();
            foreach (var raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#' || line == Magic) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string k = line.Substring(0, eq).Trim();
                string v = line.Substring(eq + 1).Trim();
                switch (k)
                {
                    case KeyClientId:     c.ClientId = v; break;
                    case KeyClientSecret: c.ClientSecret = v; break;
                    case KeyRefreshToken: c.RefreshToken = v; break;
                    case KeyRootFolderId: c.RootFolderId = v; break;
                    case KeyGithubPat:    c.GithubPat = v; break;
                    // default: ignored on purpose (forward compatible)
                }
            }
            return c;
        }

        // ---- write -----------------------------------------------------------

        /// <summary>Store (or, with an empty value, drop) the Google refresh token.</summary>
        public static void SetRefreshToken(string token) => Upsert(KeyRefreshToken, token ?? "");

        static void Upsert(string key, string value)
        {
            if (value.IndexOf('\n') >= 0 || value.IndexOf('\r') >= 0)
                throw new ArgumentException($"Value of '{key}' must not contain a line break.");

            string path = FilePath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return; // nothing attached

            lock (Gate)
            {
                var lines = new List<string>(File.ReadAllLines(path, Encoding.UTF8));
                WriteAtomic(path, UpsertLines(lines, key, value));
            }
        }

        /// <summary>Set (or, with an empty value, delete) a key, leaving comments,
        /// blank lines and unknown keys exactly where they were.</summary>
        static List<string> UpsertLines(List<string> lines, string key, string value)
        {
            bool found = false;
            for (int i = 0; i < lines.Count; i++)
            {
                string t = lines[i].Trim();
                if (t.Length == 0 || t[0] == '#') continue;
                int eq = t.IndexOf('=');
                if (eq <= 0 || t.Substring(0, eq).Trim() != key) continue;

                if (value.Length == 0) { lines.RemoveAt(i); i--; } // clearing removes the line
                else lines[i] = key + " = " + value;
                found = true;
            }
            if (!found && value.Length > 0) lines.Add(key + " = " + value);
            return lines;
        }

        // Temp file + replace: a crash mid-write must not leave the user with a
        // truncated credential file and no way back.
        static void WriteAtomic(string path, List<string> lines)
        {
            string tmp = path + ".tmp";
            try
            {
                File.WriteAllLines(tmp, lines, new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
            }
            finally
            {
                // A failed write must not leave a half-finished .tmp beside the real file.
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best effort */ }
            }
        }

        /// <summary>Write a new credential file. Empty values still get their line and
        /// comment, so the result doubles as a fill-in-later template.</summary>
        public static void Create(string path, Credentials c)
        {
            var lines = BuildFile(c); // may throw on a bad value: do that before touching disk
            lock (Gate) WriteAtomic(path, lines);
        }

        static List<string> BuildFile(Credentials c)
        {
            string rootId = FolderIdFrom(c.RootFolderId);
            var lines = new List<string>
            {
                Magic,
                "# MyUnityHub credential file.",
                "#",
                "# KEEP THIS FILE PRIVATE. Store it outside the Unity project and do NOT",
                "# commit it. The editor remembers only this file's path; none of the",
                "# values below are ever copied into Unity's own settings.",
                "#",
                "# Fill in the blanks here, then hit 'Reload' in Unity.",
                "",
                "# Google Cloud > OAuth client (application type: Desktop app)",
                Line(KeyClientId, c.ClientId),
                Line(KeyClientSecret, c.ClientSecret),
                "",
                "# The id in the Drive folder URL",
                Line(KeyRootFolderId, rootId),
                "",
                "# GitHub personal access token (repo or public_repo)",
                Line(KeyGithubPat, c.GithubPat),
                "",
                "# Do not fill the line below in by hand: the editor writes it on sign-in.",
            };
            if (c.RefreshToken.Length > 0) lines.Add(Line(KeyRefreshToken, c.RefreshToken));
            else lines.Add("# " + KeyRefreshToken + " = ");
            return lines;
        }

        static string Line(string key, string value)
        {
            value = (value ?? "").Trim();
            if (value.IndexOf('\n') >= 0 || value.IndexOf('\r') >= 0)
                throw new ArgumentException($"Value of '{key}' must not contain a line break.");
            return key + " = " + value;
        }

        /// <summary>Accept either a bare folder id or a pasted Drive folder URL
        /// ("https://drive.google.com/drive/folders/&lt;id&gt;?usp=sharing").</summary>
        public static string FolderIdFrom(string idOrUrl)
        {
            string s = (idOrUrl ?? "").Trim();
            const string marker = "/folders/";
            int i = s.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return s;
            string rest = s.Substring(i + marker.Length);
            int cut = rest.IndexOfAny(new[] { '?', '/', '&', '#' });
            return cut < 0 ? rest : rest.Substring(0, cut);
        }

        // ---- one-time migration off EditorPrefs -------------------------------

        public static bool HasLegacyPrefs
        {
            get
            {
                foreach (var k in LegacyKeys)
                    if (!string.IsNullOrEmpty(EditorPrefs.GetString(k, ""))) return true;
                return false;
            }
        }

        /// <summary>Move secrets that older versions left in EditorPrefs into a file,
        /// then erase them from EditorPrefs.</summary>
        public static void MigrateLegacyPrefsTo(string path)
        {
            var lines = new List<string>
            {
                Magic,
                "# MyUnityHub - migrated out of Unity's EditorPrefs.",
                "# KEEP THIS FILE PRIVATE: store it outside the project, do not commit it.",
                "",
            };
            AddIfSet(lines, KeyClientId,     "DriveGitHubHub.clientId");
            AddIfSet(lines, KeyClientSecret, "DriveGitHubHub.clientSecret");
            AddIfSet(lines, KeyRootFolderId, "DriveGitHubHub.rootFolderId");
            AddIfSet(lines, KeyGithubPat,    "DriveGitHubHub.githubPat");
            AddIfSet(lines, KeyRefreshToken, "DriveGitHubHub.refresh_token");

            lock (Gate) WriteAtomic(path, lines);
            FilePath = path;
            ClearLegacyPrefs();
        }

        static void AddIfSet(List<string> lines, string key, string legacyKey)
        {
            string v = EditorPrefs.GetString(legacyKey, "");
            if (!string.IsNullOrEmpty(v)) lines.Add(key + " = " + v);
        }

        public static void ClearLegacyPrefs()
        {
            foreach (var k in LegacyKeys) EditorPrefs.DeleteKey(k);
        }

        // ---- self-check --------------------------------------------------------
        // Tools > MyUnityHub > Run Credential Format Test. Covers the parse/upsert
        // rules only - no file IO, no EditorPrefs, nothing to clean up afterwards.

        [MenuItem("Tools/MyUnityHub/Run Credential Format Test", false, 40)]
        public static void SelfCheck()
        {
            void Check(bool ok, string what)
            {
                if (!ok) throw new Exception("FAIL: " + what);
            }

            // parse: comments, blank lines, spacing, '=' inside the value, unknown keys
            var c = Parse(new[]
            {
                Magic,
                "# a comment line",
                "",
                "  " + KeyClientId + "  =  abc123  ",
                KeyClientSecret + "=s3cr=et",
                "some.future.key = ignored",
                KeyGithubPat + " = ghp_xyz",
            }, "test");
            Check(c.ClientId == "abc123", "clientId trimmed");
            Check(c.ClientSecret == "s3cr=et", "value keeps '=' after the first one");
            Check(c.GithubPat == "ghp_xyz", "pat parsed");
            Check(c.RootFolderId == "", "missing key stays empty");
            Check(c.HasGoogle && c.HasGithub, "presence flags");

            // parse: a file without the magic line is refused
            bool refused = false;
            try { Parse(new[] { KeyGithubPat + " = x" }, "test"); }
            catch (IOException) { refused = true; }
            Check(refused, "missing magic rejected");

            // upsert: replace in place, append when absent, delete when cleared
            var lines = new List<string> { Magic, "# keep me", KeyClientId + " = old" };
            UpsertLines(lines, KeyClientId, "new");
            Check(lines[2] == KeyClientId + " = new", "existing key replaced in place");
            Check(lines[1] == "# keep me", "comments preserved");

            UpsertLines(lines, KeyRefreshToken, "rt1");
            Check(lines.Count == 4 && lines[3] == KeyRefreshToken + " = rt1", "absent key appended");

            UpsertLines(lines, KeyRefreshToken, "");
            Check(lines.Count == 3, "cleared key removed");
            Check(Parse(lines.ToArray(), "test").RefreshToken == "", "round-trips back to empty");

            // a commented-out key is not a key: it must be appended, not uncommented
            var commented = new List<string> { Magic, "# " + KeyRefreshToken + " = " };
            UpsertLines(commented, KeyRefreshToken, "rt2");
            Check(commented.Count == 3, "commented key left alone, real one appended");

            // pasted Drive folder URL is reduced to the bare id
            Check(FolderIdFrom("1AbC_xyz") == "1AbC_xyz", "bare id passes through");
            Check(FolderIdFrom("https://drive.google.com/drive/folders/1AbC_xyz?usp=sharing")
                  == "1AbC_xyz", "id extracted from url with query");
            Check(FolderIdFrom(" https://drive.google.com/drive/folders/1AbC_xyz ")
                  == "1AbC_xyz", "id extracted from trimmed url");

            // a file built by the wizard parses back to exactly what went in, and an
            // empty one is still a valid template
            var made = Parse(BuildFile(new Credentials
            {
                ClientId = "cid", ClientSecret = "sec",
                RootFolderId = "https://drive.google.com/drive/folders/fid",
                GithubPat = "pat",
            }).ToArray(), "test");
            Check(made.ClientId == "cid" && made.ClientSecret == "sec", "google round-trip");
            Check(made.RootFolderId == "fid", "url normalised on write");
            Check(made.GithubPat == "pat", "pat round-trip");
            Check(made.RefreshToken == "", "no refresh token written for a new file");

            var blank = Parse(BuildFile(new Credentials()).ToArray(), "test");
            Check(!blank.HasGoogle && !blank.HasGithub, "empty file is a valid template");

            Debug.Log("MyUnityHub: credential file format test passed.");
        }
    }
}
