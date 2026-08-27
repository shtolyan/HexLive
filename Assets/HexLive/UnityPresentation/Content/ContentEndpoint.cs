using System;
using HexLive.UnityPresentation.Bootstrap;

namespace HexLive.UnityPresentation.Content
{

public static class ContentEndpoint
{
    private const string Argument = "-hexlive-assets";

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
                Uri.TryCreate(server, UriKind.Absolute, out _))
            {
                return FromGameServer(server);
            }

            // Registry/music start while the main menu is still open, before
            // the player has selected a simulation server. Content is not a
            // localhost build sidecar: the normal bootstrap source is prod.
            // A developer who really runs a local Asset API opts in explicitly
            // with -hexlive-assets.
            return FromGameServer(ServerBook.ProductionUrl);
        }
    }

    private static string FromGameServer(string server)
    {
        var websocket = new Uri(server, UriKind.Absolute);
        var builder = new UriBuilder(websocket)
        {
            Scheme = websocket.Scheme == "wss" ? "https" : "http",
            Path = "/api/assets/v1",
            Query = string.Empty,
            Fragment = string.Empty,
        };
        return builder.Uri.ToString().TrimEnd('/');
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
