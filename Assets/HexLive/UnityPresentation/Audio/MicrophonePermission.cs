using System.Runtime.InteropServices;
using UnityEngine;

namespace HexLive.UnityPresentation.Audio
{
    /// <summary>§160: macOS authorization only; recording stays in FMOD.</summary>
    internal static class MicrophonePermission
    {
#if UNITY_STANDALONE_OSX && !UNITY_EDITOR
        [DllImport("HexLivePermissions")] private static extern int HexLiveMicrophoneStatus();
        [DllImport("HexLivePermissions")] private static extern void HexLiveRequestMicrophone();
#endif
        internal static void RequestAtStartup()
        {
#if UNITY_STANDALONE_OSX && !UNITY_EDITOR
            HexLiveRequestMicrophone();
#endif
        }
        internal static bool Pending => Status == 0;
        internal static bool Granted => Status == 3;
        private static int Status
        {
            get
            {
#if UNITY_STANDALONE_OSX && !UNITY_EDITOR
                return HexLiveMicrophoneStatus();
#else
                return 3;
#endif
            }
        }
    }
}
