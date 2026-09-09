using System;
using System.Collections;
using System.Collections.Generic;
using HexLive.UnityPresentation.Audio;
using HexLive.UnityPresentation.UI;
using UnityEngine;

namespace HexLive.UnityPresentation.Bootstrap
{
    /// <summary>§160: resolve OS access in the menu, before the first conversation.</summary>
    internal sealed class StartupAccessPolicy : MonoBehaviour
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Initialize()
        {
            if (Application.isEditor) return;
            var host = new GameObject("HexLiveStartupAccess");
            DontDestroyOnLoad(host);
            host.AddComponent<StartupAccessPolicy>();
        }

        private IEnumerator Start()
        {
            yield return null;
            var permissionAvailable = true;
            try { MicrophonePermission.RequestAtStartup(); }
            catch (Exception ex)
            {
                permissionAvailable = false;
                Debug.LogWarning("[StartupAccess] Microphone permission bridge unavailable: " + ex.GetType().Name);
            }
            if (permissionAvailable)
            {
                while (MicrophonePermission.Pending) yield return null;
                Debug.Log("[StartupAccess] Microphone authorized: " + MicrophonePermission.Granted);
            }
            var servers = new HashSet<string>(ServerBook.Recent(), StringComparer.Ordinal);
            if (!string.IsNullOrWhiteSpace(ServerBook.LastUrl)) servers.Add(ServerBook.LastUrl);
            if (!string.IsNullOrWhiteSpace(SessionConfig.ServerUrl)) servers.Add(SessionConfig.ServerUrl);
            foreach (var server in servers)
            {
                // Missing credentials are cached but not generated until that server is used.
                try { AdminCredentialStore.Read(server, SessionConfig.ClientId); }
                catch (Exception) { Debug.LogWarning("[StartupAccess] Saved credential unavailable for this session."); }
                yield return null;
            }
            Destroy(gameObject);
        }
    }
}
