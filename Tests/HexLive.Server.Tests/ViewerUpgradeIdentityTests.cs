using System;
using HexLive.Server;
using NUnit.Framework;

namespace HexLive.Server.Tests
{

/// <summary>
/// §168.2: токен и id клиента приходят в <c>/watch</c> заголовками (десктоп)
/// или subprotocol'ами (браузер). Заголовок главнее; битый base64url —
/// анонимный зритель, а не отказ; без маркера hexlive.v1 сервер не выбирает
/// subprotocol и не включает deflate.
/// </summary>
public sealed class ViewerUpgradeIdentityTests
{
    [Test]
    public void DesktopHeadersAreReadAsBefore()
    {
        var identity = ViewerUpgradeIdentity.Resolve(
            "Bearer hexplay_abc ", " client-1 ", "gzip", Array.Empty<string>());

        Assert.That(identity.Token, Is.EqualTo("hexplay_abc"));
        Assert.That(identity.ClientId, Is.EqualTo("client-1"));
        Assert.That(identity.AcceptsGzip, Is.True);
        Assert.That(identity.WebProtocol, Is.False);
    }

    [Test]
    public void BrowserSubprotocolsCarryTokenAndClient()
    {
        var identity = ViewerUpgradeIdentity.Resolve(
            string.Empty, string.Empty, string.Empty,
            new[]
            {
                "hexlive.v1",
                "hexlive.token." + ViewerUpgradeIdentity.EncodeBase64Url("key/with+odd=chars"),
                "hexlive.client." + ViewerUpgradeIdentity.EncodeBase64Url("0123abcd"),
            });

        Assert.That(identity.Token, Is.EqualTo("key/with+odd=chars"));
        Assert.That(identity.ClientId, Is.EqualTo("0123abcd"));
        Assert.That(identity.AcceptsGzip, Is.False);
        Assert.That(identity.WebProtocol, Is.True);
    }

    [Test]
    public void KnownBase64UrlVectorsDecode()
    {
        // "??>" = 3F 3F 3E -> "Pz8+" -> url "Pz8-"; "???" -> "Pz8/" -> "Pz8_".
        // Written out by hand so the decoder is not only checked against its
        // own encoder (the browser side encodes in C# on another runtime).
        var identity = ViewerUpgradeIdentity.Resolve(
            null, null, null, new[] { "hexlive.v1", "hexlive.token.Pz8-", "hexlive.client.Pz8_" });

        Assert.That(identity.Token, Is.EqualTo("??>"));
        Assert.That(identity.ClientId, Is.EqualTo("???"));
    }

    [Test]
    public void HeaderWinsOverSubprotocol()
    {
        var identity = ViewerUpgradeIdentity.Resolve(
            "Bearer from-header", "header-client", null,
            new[] { "hexlive.v1", "hexlive.token." + ViewerUpgradeIdentity.EncodeBase64Url("from-protocol") });

        Assert.That(identity.Token, Is.EqualTo("from-header"));
        Assert.That(identity.ClientId, Is.EqualTo("header-client"));
        Assert.That(identity.WebProtocol, Is.True);
    }

    [Test]
    public void BrokenEncodingIsAnonymousNotARefusal()
    {
        var identity = ViewerUpgradeIdentity.Resolve(
            null, null, null, new[] { "hexlive.v1", "hexlive.token.a", "hexlive.client.!!" });

        Assert.That(identity.Token, Is.Null);
        Assert.That(identity.ClientId, Is.Null);
        Assert.That(identity.WebProtocol, Is.True);
    }

    [Test]
    public void CommaJoinedProtocolHeaderIsSplit()
    {
        var identity = ViewerUpgradeIdentity.Resolve(
            null, null, null,
            new[] { "hexlive.v1, hexlive.token." + ViewerUpgradeIdentity.EncodeBase64Url("t") });

        Assert.That(identity.WebProtocol, Is.True);
        Assert.That(identity.Token, Is.EqualTo("t"));
    }

    [Test]
    public void WithoutMarkerTheSocketIsNotTheWebPath()
    {
        var identity = ViewerUpgradeIdentity.Resolve(
            null, null, null, new[] { "hexlive.token." + ViewerUpgradeIdentity.EncodeBase64Url("t") });

        Assert.That(identity.WebProtocol, Is.False);
        Assert.That(identity.Token, Is.EqualTo("t"));
    }

    [Test]
    public void NonBearerAuthorizationIsIgnored()
    {
        var identity = ViewerUpgradeIdentity.Resolve("Basic abc", null, null, null);

        Assert.That(identity.Token, Is.Null);
    }
}

}
