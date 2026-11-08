using System;

namespace HexLive.UnityPresentation.UI
{
    public static class BugReportConnectionPolicy
    {
        public static bool IsWriteOnlyReporter(string project, bool admin, string[] statuses) =>
            project == "hexlive" && !admin && statuses != null && statuses.Length == 1 && statuses[0] == "created";
        public static bool IsApi(string address) => Uri.TryCreate(address, UriKind.Absolute, out var uri) &&
            uri.Scheme == "https" && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) &&
            string.IsNullOrEmpty(uri.Fragment) && uri.AbsolutePath.TrimEnd('/') == "/api/bugs/v1";
    }
}
