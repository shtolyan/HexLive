#if UNITY_EDITOR
using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using HexLive.UnityPresentation.UI;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

// §114.8: never ship an agent's key as a game capability.
public sealed class HexLiveBugReporterBuild : IPreprocessBuildWithReport
{
    public int callbackOrder => -2000;
    [Serializable] private sealed class Identity
    { public string reporterProject; public bool isAdmin; public string[] allowedStatuses; }
    public void OnPreprocessBuild(BuildReport report) => Prepare();
    public static void Prepare()
    {
        const string destination = "Assets/Resources/HexLive/RuntimeReporterConnection.json";
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "hexlive", "reporting.json");
        if (!File.Exists(path)) throw new BuildFailedException("Missing device-local HexLive reporting.json. Provision a write-only hexlive reporter capability; never use the agent token.");
        BugReportSender.Connection value;
        try { value = JsonUtility.FromJson<BugReportSender.Connection>(File.ReadAllText(path)); }
        catch { throw new BuildFailedException("Invalid local reporting.json."); }
        if (value == null || value.project != "hexlive" || !BugReportConnectionPolicy.IsApi(value.api) || string.IsNullOrWhiteSpace(value.token))
            throw new BuildFailedException("Invalid reporter endpoint, project or capability.");
        try
        {
            var endpoint = new Uri(value.api);
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint.GetLeftPart(UriPartial.Authority) + "/api/auth/me");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", value.token);
            using var response = http.SendAsync(request).GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException();
            var identity = JsonUtility.FromJson<Identity>(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            if (identity == null || !BugReportConnectionPolicy.IsWriteOnlyReporter(identity.reporterProject, identity.isAdmin, identity.allowedStatuses))
                throw new InvalidOperationException();
        }
        catch { throw new BuildFailedException("Reporter verification failed. A verified write-only hexlive capability is required; no admin/agent key may be packaged."); }
        File.WriteAllText(destination, JsonUtility.ToJson(value));
        AssetDatabase.ImportAsset(destination, ImportAssetOptions.ForceSynchronousImport);
    }
}
#endif
