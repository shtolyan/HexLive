using System;
using System.IO;
using HexLive.UnityPresentation.UI;
using NUnit.Framework;

namespace HexLive.Server.Tests;
public sealed class AdminDeviceCredentialTests
{
    [Test] public void EmptyStoreGetsRandomProofAndReusesItForTheSameDevice()
    {
        string stored = ""; int writes = 0;
        var proof = AdminDeviceCredential.ReadOrCreate(() => stored, x => { stored = x; writes++; });
        Assert.That(proof, Does.Match("^[0-9A-F]{64}$"));
        Assert.That(AdminDeviceCredential.ReadOrCreate(() => stored, _ => writes++), Is.EqualTo(proof));
        Assert.That(writes, Is.EqualTo(1));
        Assert.That(AdminDeviceCredential.ReadOrCreate(() => "", _ => { }), Is.Not.EqualTo(proof));
    }
    [TestCase("hexlive_player_access_key")]
    [TestCase("gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    public void NonDeviceCredentialCannotBeSilentlyReplaced(string existing)
    {
        Assert.Throws<InvalidOperationException>(() =>
            AdminDeviceCredential.ReadOrCreate(() => existing, _ => Assert.Fail("must not overwrite")));
    }
    [Test] public void DeniedStorageDoesNotCreateFallbackIdentity()
    {
        Assert.Throws<IOException>(() => AdminDeviceCredential.ReadOrCreate(
            () => throw new IOException(), _ => Assert.Fail("must not write")));
        Assert.Throws<IOException>(() => AdminDeviceCredential.ReadOrCreate(
            () => "", _ => throw new IOException()));
    }
}
