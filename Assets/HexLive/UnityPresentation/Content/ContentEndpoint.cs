using System;
using HexLive.UnityPresentation.Bootstrap;

namespace HexLive.UnityPresentation.Content
{

public static class ContentEndpoint
{
    private const string Argument = "-hexlive-assets";
    private const string DefaultLocal = "http://localhost:5123/api/assets/v1";

    public static string Current
    {
        get
        {
            var explicitValue = CommandLineValue();
            if (!string.IsNullOrWhiteSpace(explicitValue))
            {
                return explicitValue.TrimEnd('/');
            }

            var server = SessionConfig.ServerUrl;
            if (!string.IsNullOrWhiteSpace(server) &&
                Uri.TryCreate(server, UriKind.Absolute, out var websocket))
            {
                var builder = new UriBuilder(websocket)
                {
                    Scheme = websocket.Scheme == "wss" ? "https" : "http",
                    Path = "/api/assets/v1",
                    Query = string.Empty,
                    Fragment = string.Empty,
                };
                return builder.Uri.ToString().TrimEnd('/');
            }

            return DefaultLocal;
        }
    }

    private static string CommandLineValue()
    {
        try
        {
            var arguments = System.Environment.GetCommandLineArgs();
            for (var index = 0; index + 1 < arguments.Length; index++)
            {
                if (string.Equals(arguments[index], Argument, StringComparison.OrdinalIgnoreCase))
                {
                    return arguments[index + 1];
                }
            }
        }
        catch (Exception)
        {
        }

        return null;
    }
}

}
