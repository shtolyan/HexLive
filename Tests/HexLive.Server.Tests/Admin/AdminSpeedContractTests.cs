using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using HexLive.Server.Assets;
using System.Threading.Tasks;
using HexLive.Server.Admin;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace HexLive.Server.Tests.Admin
{

public sealed class AdminSpeedContractTests
{
    [Test]
    public async Task SpeedPost_ChangesSpeedAndRedirectsToCanonicalDashboard()
    {
        var sessions = new AdminSessions();
        var token = sessions.CreateSession();
        var context = new DefaultHttpContext();
        context.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        context.Request.Method = "POST";
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Request.Headers.Cookie = "hexlive_admin=" + token;
        var body = Encoding.UTF8.GetBytes("speed=16");
        context.Request.Body = new MemoryStream(body);
        context.Request.ContentLength = body.Length;
        context.Response.Body = new MemoryStream();

        var applied = 0f;
        var task = InvokePrivate<Task<IResult>>(typeof(AdminEndpoints),
            "HandleSpeed", context, sessions, new Action<float>(value => applied = value));
        var result = await task;
        await result.ExecuteAsync(context);

        Assert.Multiple(() =>
        {
            Assert.That(applied, Is.EqualTo(16f));
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status302Found));
            Assert.That(context.Response.Headers.Location.ToString(), Is.EqualTo("/admin"));
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

    [Test]
    public void WearCatalogCardEscapesMetadataAndExposesAllSimulationFields()
    {
        var record = new ContentObjectRecord
        {
            Type = "wear",
            Id = "skirt.anarchy",
            Revision = 7,
            State = "active",
            PublishedAtUtc = DateTimeOffset.UtcNow,
        };
        var entry = new GarmentCatalogEntry
        {
            Record = record,
            ConfigurationSource = "record",
            Simulation = new GarmentSimulationMetadata
            {
                DisplayName = "<script>alert(1)</script>",
                PrototypeId = "skirt.anarchy",
                Layer = "Wear",
                Sex = "Female",
                Covers = new List<string> { "Pelvis" },
            },
        };

        var html = AdminPages.CatalogRecord(
            record, new[] { record }, entry, liveRevision: 10, pinnedRevision: 9, notice: null);

        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Not.Contain("<script>alert(1)</script>"));
            Assert.That(html, Does.Contain("&lt;script&gt;alert(1)&lt;/script&gt;"));
            Assert.That(html, Does.Contain("name='warmth'"));
            Assert.That(html, Does.Contain("name='armor'"));
            Assert.That(html, Does.Contain("name='thermalDelta'"));
            Assert.That(html, Does.Contain("name='covers'"));
            Assert.That(html, Does.Contain("expectedRevision"));
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
