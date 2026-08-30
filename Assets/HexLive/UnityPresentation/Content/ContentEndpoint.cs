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
                return EditorFriendly(FromGameServer(server));
            }

            // Registry/music start while the main menu is still open, before
            // the player has selected a simulation server. Content is not a
            // localhost build sidecar: the normal bootstrap source is prod.
            // A developer who really runs a local Asset API opts in explicitly
            // with -hexlive-assets.
            return EditorFriendly(FromGameServer(ServerBook.ProductionUrl));
        }
    }

    /// <summary>
    /// РЕДАКТОР не умеет в прод-HTTPS: UnityTls не проходит цепочку
    /// Let's Encrypt-сертификата (Curl error 35), и каждая шторка вешалась
    /// на «Проверяем …». Внутри редактора публичный HTTPS-host подменяется
    /// legacy-HTTP origin ТОГО ЖЕ сервера (Kestrel на 5123 — §152.4 держит
    /// его именно на переходный период). Только прод-host: локальные и свои
    /// сервера не трогаются; Player этой ветки не имеет вовсе.
    /// </summary>
    private static string EditorFriendly(string endpoint)
    {
#if UNITY_EDITOR
        var productionHost = new Uri(ServerBook.ProductionUrl, UriKind.Absolute).Host;
        if (endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
            new Uri(endpoint, UriKind.Absolute).Host.Equals(
                productionHost, StringComparison.OrdinalIgnoreCase))
        {
            return FromGameServer(ServerBook.LegacyProductionUrl);
        }
#endif
        return endpoint;
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
