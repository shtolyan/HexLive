using System;
using System.Reflection;
using HexLive.Server.Admin;
using NUnit.Framework;

namespace HexLive.Server.Tests.Admin
{

public sealed class AdminSpeedContractTests
{
    [Test]
    public void SpeedRedirectLocation_EncodesUnicodeAndStaysAscii()
    {
        var location = InvokePrivate<string>(typeof(AdminEndpoints),
            "SpeedRedirectLocation", 4f);

        Assert.Multiple(() =>
        {
            Assert.That(location, Is.EqualTo("/admin?notice=Speed%20set%20to%204%C3%97."));
            Assert.That(location, Does.Not.Match("[^\\x00-\\x7F]"));
        });
    }

    [Test]
    public void SpeedButton_HighlightsOnlyTheActualCurrentSpeed()
    {
        var selected = InvokePrivate<string>(typeof(AdminPages), "SpeedButton", 16f, 16f);
        var other = InvokePrivate<string>(typeof(AdminPages), "SpeedButton", 16f, 4f);

        Assert.Multiple(() =>
        {
            Assert.That(selected, Does.Contain("class='primary'"));
            Assert.That(selected, Does.Contain("aria-current='true'"));
            Assert.That(selected, Does.Contain("value='16'"));
            Assert.That(other, Does.Not.Contain("class='primary'"));
            Assert.That(other, Does.Not.Contain("aria-current"));
        });
    }

    private static T InvokePrivate<T>(Type owner, string methodName, params object[] arguments)
    {
        var method = owner.GetMethod(methodName,
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.That(method, Is.Not.Null, $"Missing {owner.Name}.{methodName}");
        return (T)method!.Invoke(null, arguments)!;
    }
}

}
