using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace MyUnityHub
{
    /// <summary>
    /// Builds a .myhub credential file. What is typed here goes straight to the file
    /// the user picks and is dropped from memory afterwards - the wizard never writes
    /// to EditorPrefs, and the fields are not serialized, so a domain reload clears
    /// them too.
    /// </summary>
    internal class CredentialFileWizard : EditorWindow
    {
        // deliberately not [SerializeField]: secrets must not survive a domain reload
        string _clientId = "", _secret = "", _rootId = "", _pat = "";
        bool _reveal;
        string _error;

        [MenuItem("Tools/MyUnityHub/New Credential File...", false, 20)]
        public static void Open()
        {
            var w = GetWindow<CredentialFileWizard>(true, "New Credential File", true);
            w.minSize = new Vector2(460, 340);
            w.ShowUtility();
        }

        void OnGUI()
        {
            EditorGUILayout.HelpBox(
                "What you type here is written only to the file you pick. Nothing is " +
                "saved to Unity's settings, and the fields are cleared when this window " +
                "closes.", MessageType.Info);

            EditorGUILayout.Space();
            _reveal = EditorGUILayout.ToggleLeft("Show values", _reveal);
            EditorGUILayout.Space();

            EditorGUILayout.LabelField("Google Drive", EditorStyles.boldLabel);
            _clientId = Field("Client ID", _clientId);
            _secret   = Field("Client Secret", _secret);
            _rootId   = Field("Root Folder ID / URL", _rootId);
            EditorGUILayout.LabelField(" ", "You can paste the whole folder URL; the id is extracted.",
                EditorStyles.miniLabel);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("GitHub", EditorStyles.boldLabel);
            _pat = Field("Personal Access Token", _pat);

            EditorGUILayout.Space();
            string missing = Missing();
            if (missing != null)
                EditorGUILayout.HelpBox(
                    missing + "\nYou can leave it blank: the file still gets the line, " +
                    "ready to fill in later in a text editor.", MessageType.Warning);

            if (_error != null) EditorGUILayout.HelpBox(_error, MessageType.Error);

            GUILayout.FlexibleSpace();
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Cancel", GUILayout.Height(24))) Close();
                if (GUILayout.Button("Save and Attach...", GUILayout.Height(24))) Save();
            }
        }

        string Field(string label, string value) =>
            _reveal ? EditorGUILayout.TextField(label, value)
                    : EditorGUILayout.PasswordField(label, value);

        /// <summary>Which half of the tool would not work with what has been entered.</summary>
        string Missing()
        {
            bool google = _clientId.Trim().Length > 0 && _secret.Trim().Length > 0;
            bool github = _pat.Trim().Length > 0;
            if (google && github) return null;
            if (!google && !github) return "Neither the Google nor the GitHub values are filled in.";
            return google ? "No GitHub PAT: the GitHub / UPM tab will not work."
                          : "Google Client ID/Secret missing: the Drive tab will not work.";
        }

        void Save()
        {
            // Default outside the project: the file must not end up in version control.
            string dir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string path = EditorUtility.SaveFilePanel("Save Credential File",
                dir, "myunityhub." + CredentialStore.Extension, CredentialStore.Extension);
            if (string.IsNullOrEmpty(path)) return;

            if (CredentialStore.IsInsideProject(path) &&
                !EditorUtility.DisplayDialog("Saving inside the project",
                    "This location is inside the Unity project, so the file could be " +
                    "committed by accident.\n\n" + path, "Save anyway", "Cancel"))
                return;

            try
            {
                CredentialStore.Create(path, new Credentials
                {
                    ClientId = _clientId,
                    ClientSecret = _secret,
                    RootFolderId = _rootId,
                    GithubPat = _pat,
                });
                CredentialStore.FilePath = path;
            }
            catch (Exception e)
            {
                _error = e.Message;
                return;
            }

            _clientId = _secret = _rootId = _pat = ""; // do not linger in memory
            HubWindow.ReloadAllOpen();
            Close();
            GUIUtility.ExitGUI();
        }

        void OnDestroy() => _clientId = _secret = _rootId = _pat = "";
    }
}
