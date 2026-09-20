using System;

namespace HexLive.UnityPresentation.UI
{
    [Serializable]
    internal sealed class ClientReleaseOffer
    {
        public string version;
        public ReleasePayload playerRelease;

        [Serializable]
        internal sealed class ReleasePayload { public int protocolVersion; }

        public bool IsNewerCompatible(string installed, int protocol) =>
            playerRelease != null && playerRelease.protocolVersion == protocol &&
            Version.TryParse(version, out var available) &&
            Version.TryParse(installed, out var current) && available > current;
    }
}
