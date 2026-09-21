using UnityEngine;
using UnityEngine.Networking;
using System;
using System.Collections;
using HexLive.UnityPresentation.UI;

namespace HexLive.UnityPresentation.Bootstrap
{
    /// <summary>§166: release entry points; legacy local tools remain in the Editor.</summary>
    public static class ClosedTestAccess
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void StartPermissions()
        {
            if (!Enabled) return;
            var host = new GameObject("Account permissions");
            UnityEngine.Object.DontDestroyOnLoad(host); host.AddComponent<AccountPermissionMonitor>();
        }
        public static bool Enabled => !Application.isEditor;
        public const string Authority = "https://keys.62-146-235-120.sslip.io";
        public static readonly string[] Servers = {
            "https://vmi3529459.contaboserver.net"
        };
        public static string[] Permissions { get; set; } = System.Array.Empty<string>();
        public static bool Can(string permission) => System.Array.IndexOf(Permissions, permission) >= 0;
        public static string ReadKey() => AdminCredentialStore.Read(Authority, "closed-test-player");
        public static void SaveKey(string key) => AdminCredentialStore.Write(Authority, "closed-test-player", key);
    }
    internal sealed class AccountPermissionMonitor : MonoBehaviour
    {
        [Serializable] private sealed class Reply { public string[] permissions; }
        private IEnumerator Start()
        {
            while (true)
            {
                string key = "";
                try { key = ClosedTestAccess.ReadKey(); } catch (Exception) { }
                if (key.Length > 0)
                {
                    using var request = new UnityWebRequest(ClosedTestAccess.Authority + "/api/identity/v1/validate", "POST");
                    request.downloadHandler = new DownloadHandlerBuffer(); request.timeout = 5; request.redirectLimit = 0;
                    request.SetRequestHeader("Authorization", "Bearer " + key);
                    yield return request.SendWebRequest();
                    string[] permissions = Array.Empty<string>();
                    if (request.result == UnityWebRequest.Result.Success)
                        try { permissions = JsonUtility.FromJson<Reply>(request.downloadHandler.text)?.permissions ?? permissions; } catch (Exception) { }
                    ClosedTestAccess.Permissions = permissions;
                }
                else ClosedTestAccess.Permissions = Array.Empty<string>();
                yield return new WaitForSecondsRealtime(15);
            }
        }
    }
}
