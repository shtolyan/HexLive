using System;
using System.IO;
using System.Diagnostics;
using HexLive.UnityPresentation.Localization;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UIElements;

namespace HexLive.UnityPresentation.UI
{
    internal sealed class ProtocolUpdatePanel : VisualElement
    {
        [Serializable] private sealed class Compatibility { public int protocolVersion; }
        public static int ReadProtocol(UnityWebRequest request)
        {
            if (request.result != UnityWebRequest.Result.Success) return 0;
            try { return JsonUtility.FromJson<Compatibility>(request.downloadHandler.text)?.protocolVersion ?? 0; }
            catch (Exception) { return 0; }
        }

        public ProtocolUpdatePanel(int protocol)
        {
            styleSheets.Add(Resources.Load<StyleSheet>("HexLive/UI/ClosedTestMenu"));
            AddToClassList("closed-test");
            Add(new Label(Loc.Get("update.required.title")));
            Add(new Label(Loc.Get("update.required.message")));
            var error = new Label(); error.AddToClassList("closed-test-message");
            Add(error);
            Add(new Button(() => {
#if UNITY_WEBGL
                // §168.5: обновить веб-клиент — перезагрузить страницу; новый
                // билд сервер отдаёт по тому же адресу.
                Platform.WebPage.Reload();
#else
                try
                {
                    if (Application.platform != RuntimePlatform.WindowsPlayer)
                        throw new PlatformNotSupportedException();
                    var game = Directory.GetParent(Application.dataPath).FullName;
                    var helper = Path.Combine(game, "HexLiveUpdater.exe");
                    if (!File.Exists(helper)) throw new FileNotFoundException();
                    // The updater is packaged with the Player; no server-controlled executable or shell.
                    Process.Start(new ProcessStartInfo(helper) {
                        UseShellExecute = false, WorkingDirectory = game,
                        Arguments = "--update --wait-pid " + Process.GetCurrentProcess().Id +
                            " --required-protocol " + protocol
                    });
                    Application.Quit();
                }
                catch (Exception) { error.text = Loc.Get("update.start.failed"); }
#endif
            }) { text = Loc.Get("update.action") });
            if (Platform.WebPage.CanQuit)
            {
                Add(new Button(() => Application.Quit()) { text = Loc.Get("menu.quit") });
            }
        }
    }
}
