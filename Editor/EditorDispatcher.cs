using System;
using System.Collections.Generic;
using UnityEditor;

namespace MyUnityHub
{
    /// <summary>
    /// Runs actions on Unity's main thread. Network code runs on background tasks
    /// (HttpClient); anything touching AssetDatabase/PackageManager/UI must be
    /// queued here because those APIs are main-thread only.
    /// </summary>
    [InitializeOnLoad]
    internal static class EditorDispatcher
    {
        static readonly Queue<Action> _queue = new Queue<Action>();

        static EditorDispatcher()
        {
            EditorApplication.update += Pump;
        }

        public static void Enqueue(Action a)
        {
            if (a == null) return;
            lock (_queue) _queue.Enqueue(a);
        }

        static void Pump()
        {
            // ponytail: drain whole queue each tick; fine for editor tool volumes.
            while (true)
            {
                Action a;
                lock (_queue)
                {
                    if (_queue.Count == 0) return;
                    a = _queue.Dequeue();
                }
                try { a(); } catch (Exception e) { UnityEngine.Debug.LogException(e); }
            }
        }
    }
}

