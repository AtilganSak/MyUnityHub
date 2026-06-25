using System;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;

namespace MyUnityHub
{
    /// <summary>
    /// Adds a git repo as a UPM package. Client.Add writes the git URL into
    /// Packages/manifest.json and resolves it natively - no manual JSON editing.
    /// </summary>
    internal static class UpmInstaller
    {
        public static void Add(string gitUrl, Action<bool, string> onDone)
        {
            var req = Client.Add(gitUrl);
            void Poll()
            {
                if (!req.IsCompleted) return;
                EditorApplication.update -= Poll;
                if (req.Status == StatusCode.Success)
                    onDone?.Invoke(true, req.Result.packageId);
                else
                    onDone?.Invoke(false, req.Error?.message ?? "unknown UPM error");
            }
            EditorApplication.update += Poll;
        }
    }
}

