using System;
using System.Collections.Generic;
using UnityEngine;

namespace HexLive.UnityPresentation.Bootstrap
{
    [Serializable]
    public sealed class ServerDirectoryEntry
    {
        public string id;
        public string nameTerm;
        public string name;
        public string socketUrl;
        public string playUrl;
        public bool enabled;

        public bool IsPageServer(string pageSocket) => ServerDirectory.SameOrigin(socketUrl, pageSocket);
    }

    // §168.13: public presentation data, never credentials or a replacement for ServerBook recents.
    public static class ServerDirectory
    {
        [Serializable] private sealed class Document { public int version; public ServerDirectoryEntry[] servers; }
        public const string ResourcePath = "HexLive/UI/ServerDirectory";
        public const string FileName = "server-directory.json";

        public static ServerDirectoryEntry[] Read(string json)
        {
            if (string.IsNullOrWhiteSpace(json) || json.Length > 65536) throw new ArgumentException("Invalid server directory size.");
            var document = JsonUtility.FromJson<Document>(json);
            if (document == null || document.version != 1 || document.servers == null ||
                document.servers.Length == 0 || document.servers.Length > 32)
                throw new ArgumentException("Invalid server directory version or entries.");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var sockets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in document.servers)
            {
                if (entry == null || string.IsNullOrWhiteSpace(entry.id) || !ids.Add(entry.id) ||
                    string.IsNullOrWhiteSpace(entry.name) || !SafeUrl(entry.socketUrl, "wss", "/watch") ||
                    !SafeUrl(entry.playUrl, "https", "/play/") || !SameOrigin(entry.socketUrl, entry.playUrl) ||
                    !sockets.Add(entry.socketUrl))
                    throw new ArgumentException("Invalid or duplicate server directory entry.");
            }
            return document.servers;
        }

        private static bool SafeUrl(string value, string scheme, string path) =>
            Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == scheme &&
            uri.AbsolutePath == path && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0;

        public static bool SameOrigin(string first, string second)
        {
            if (!Uri.TryCreate(first, UriKind.Absolute, out var a) || !Uri.TryCreate(second, UriKind.Absolute, out var b)) return false;
            var secureA = a.Scheme == "https" || a.Scheme == "wss";
            var secureB = b.Scheme == "https" || b.Scheme == "wss";
            return secureA == secureB && a.Host.Equals(b.Host, StringComparison.OrdinalIgnoreCase) && a.Port == b.Port;
        }

        public static string PingClass(int milliseconds) => milliseconds < 0 ? "offline" :
            milliseconds < 100 ? "good" : milliseconds < 250 ? "fair" : "poor";
    }
}
