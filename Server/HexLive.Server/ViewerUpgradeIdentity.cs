using System;
using System.Collections.Generic;
using System.Text;

namespace HexLive.Server
{

/// <summary>
/// §145.3/§168.2: кто стучится в <c>/watch</c> — токен игрока, id клиента и
/// «умею gzip» — из ДВУХ каналов. Десктоп шлёт их заголовками upgrade-запроса,
/// как всегда. Браузер заголовки на WebSocket ставить не умеет, поэтому
/// веб-клиент кладёт то же самое в список subprotocol'ов:
/// <c>hexlive.v1</c>, <c>hexlive.token.&lt;base64url&gt;</c>,
/// <c>hexlive.client.&lt;base64url&gt;</c>. Заголовок, если он есть, главнее.
/// <para>
/// Значения в base64url без '=': имя subprotocol допускает только tchar
/// (RFC 7230), а ключ closed test может содержать '/', '+' и '='. Битая
/// кодировка — то же, что отсутствие значения: анонимный зритель, не отказ.
/// </para>
/// </summary>
public sealed class ViewerUpgradeIdentity
{
    public const string ProtocolMarker = "hexlive.v1";
    public const string TokenProtocolPrefix = "hexlive.token.";
    public const string ClientProtocolPrefix = "hexlive.client.";

    private const string BearerPrefix = "Bearer ";

    private ViewerUpgradeIdentity(string? token, string? clientId, bool acceptsGzip, bool webProtocol)
    {
        Token = token;
        ClientId = clientId;
        AcceptsGzip = acceptsGzip;
        WebProtocol = webProtocol;
    }

    /// <summary>Предъявленный токен игрока; null — анонимный зритель.</summary>
    public string? Token { get; }

    public string? ClientId { get; }

    /// <summary>§83.4: клиент читает <c>FrameKind.Compressed</c>.</summary>
    public bool AcceptsGzip { get; }

    /// <summary>
    /// Клиент предложил <c>hexlive.v1</c>: сервер обязан выбрать его в ответе
    /// (иначе браузер оборвёт соединение) и может согласовать
    /// permessage-deflate — сжатие кадров браузеру даёт сам WebSocket.
    /// </summary>
    public bool WebProtocol { get; }

    public static ViewerUpgradeIdentity Resolve(
        string? authorizationHeader,
        string? clientIdHeader,
        string? acceptsHeader,
        IEnumerable<string>? requestedProtocols)
    {
        string? token = null;
        var authorization = authorizationHeader ?? string.Empty;
        if (authorization.StartsWith(BearerPrefix, StringComparison.Ordinal))
        {
            token = NullIfEmpty(authorization.Substring(BearerPrefix.Length).Trim());
        }

        var clientId = NullIfEmpty((clientIdHeader ?? string.Empty).Trim());
        var acceptsGzip = (acceptsHeader ?? string.Empty)
            .Contains("gzip", StringComparison.OrdinalIgnoreCase);

        var web = false;
        string? protocolToken = null;
        string? protocolClient = null;
        foreach (var raw in requestedProtocols ?? Array.Empty<string>())
        {
            // ASP.NET отдаёт список уже разрезанным по запятым, но значение из
            // одного заголовка может прийти и целиком — режем сами.
            foreach (var part in raw.Split(','))
            {
                var protocol = part.Trim();
                if (protocol == ProtocolMarker)
                {
                    web = true;
                }
                else if (protocol.StartsWith(TokenProtocolPrefix, StringComparison.Ordinal))
                {
                    protocolToken ??= DecodeBase64Url(protocol.Substring(TokenProtocolPrefix.Length));
                }
                else if (protocol.StartsWith(ClientProtocolPrefix, StringComparison.Ordinal))
                {
                    protocolClient ??= DecodeBase64Url(protocol.Substring(ClientProtocolPrefix.Length));
                }
            }
        }

        return new ViewerUpgradeIdentity(
            token ?? NullIfEmpty(protocolToken?.Trim()),
            clientId ?? NullIfEmpty(protocolClient?.Trim()),
            acceptsGzip,
            web);
    }

    public static string EncodeBase64Url(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string? DecodeBase64Url(string value)
    {
        if (value.Length == 0)
        {
            return null;
        }

        var padded = value.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
            case 1: return null;
        }

        try
        {
            return new UTF8Encoding(false, true).GetString(Convert.FromBase64String(padded));
        }
        catch (FormatException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}

}
