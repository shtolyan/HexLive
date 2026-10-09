using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using HexLive.UnityPresentation.Content;
using HexLive.Simulation.Content;
using NUnit.Framework;
using UnityEngine;

namespace HexLive.Tests
{
// Isolated service state: no disk, production endpoint or downloaded payload is
// substituted. Exercise the actual offline pin/recovery methods after a timeout.
public sealed class ContentRegistryRecoveryRuntimeTests
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
    private ContentAssetService _service;
    private object _previousInstance;
    private Dictionary<string, ContentRecord> _known, _pinned, _verified;
    private List<Action> _waiters;

    [SetUp]
    public void SetUp()
    {
        _service = (ContentAssetService)FormatterServices.GetUninitializedObject(typeof(ContentAssetService));
        _known = new(); _pinned = new(); _verified = new(); _waiters = new();
        Set("_known", _known); Set("_pinned", _pinned); Set("_verified", _verified);
        Set("_registryWaiters", _waiters);
        Set("<LastError>k__BackingField", "HTTP timeout");
        var singleton = typeof(ContentAssetService).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic);
        _previousInstance = singleton.GetValue(null);
        singleton.SetValue(null, _service);
    }

    [TearDown]
    public void TearDown()
    {
        // Recovery coroutines notice the changed instance and stop without HTTP.
        typeof(ContentAssetService).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)
            .SetValue(null, _previousInstance);
    }

    [Test]
    public void TimeoutRetainsKnownUncachedAndInflightWearAndProps()
    {
        foreach (var record in new[] { Record("wear", "clothing.skirt_primal_1"),
                     Record("object", "resource.palm_leaf"), Record("object", "tool.axe_stone") })
            _known[record.Key] = record;
        Invoke("PinOfflineRecords");
        foreach (var record in _known.Values)
        {
            Assert.That(_service.TryGetRecord(record.type, record.id, out var pinned), Is.True);
            Assert.That(pinned, Is.SameAs(record));
        }
        Assert.That(_verified, Is.Empty, "Keeping metadata must not mark bytes as verified.");
    }

    [Test]
    public void VerifiedOlderRevisionRemainsOfflineFallback()
    {
        var latest = Record("wear", "clothing.skirt_primal_1"); latest.revision = 2;
        var older = Record(latest.type, latest.id);
        _known[latest.Key] = latest; _verified[older.Key] = older;
        Invoke("PinOfflineRecords");
        Assert.That(_service.TryGetRecord(latest.type, latest.id, out var pinned), Is.True);
        Assert.That(pinned, Is.SameAs(older));
    }

    [Test]
    public void RetiredRecordsNeverReenterActiveDiscovery()
    {
        var retired = Record("wear", "old-skirt"); retired.state = "retired";
        var legacy = Record(retired.type, retired.id); legacy.state = "legacy";
        _known[retired.Key] = retired; _verified[legacy.Key] = legacy;
        Invoke("PinOfflineRecords");
        Assert.That(_service.Records("wear"), Is.Empty);
        Assert.That(_service.TryGetRecord(legacy.type, legacy.id, out _), Is.True,
            "Explicit old-save resolution may retain its verified legacy record.");
    }

    [Test]
    public void ColdFailureKeepsLoadsPendingAndSchedulesOnlyOneRecovery()
    {
        var completed = 0;
        _waiters.Add(() => completed++);
        Invoke("RecoverRegistry", "http://127.0.0.1:1/api/assets/v1");
        var version = Get<int>("_registryRetryVersion");
        Assert.That(_service.RegistryReady, Is.False);
        Assert.That(completed, Is.Zero);
        Assert.That(_waiters.Count, Is.EqualTo(1));
        Assert.That(Get<bool>("_registryRetryScheduled"), Is.True);
        Invoke("RecoverRegistry", "http://127.0.0.1:1/api/assets/v1");
        Assert.That(Get<int>("_registryRetryVersion"), Is.EqualTo(version));
        Assert.That(Get<int>("_registryFailures"), Is.EqualTo(1));

        var axe = Record("object", "tool.axe_stone"); _known[axe.Key] = axe;
        Invoke("PinKnownRecords");
        Set("<LastError>k__BackingField", string.Empty);
        Invoke("CompleteRegistry");
        Assert.That(_service.RegistryReady, Is.True);
        Assert.That(completed, Is.EqualTo(1));
        Assert.That(_waiters, Is.Empty);
        Assert.That(Get<int>("_registryFailures"), Is.Zero);
    }

    [Test]
    public void SupersededRecoveryTimerCannotRefreshAnotherEndpoint()
    {
        Set("_registryRetryVersion", 2);
        var retry = (IEnumerator)Invoke("RetryRegistry", "http://127.0.0.1:1/api/assets/v1", 1, 2f);
        Assert.That(retry.MoveNext(), Is.True);
        Assert.That(retry.Current, Is.TypeOf<WaitForSecondsRealtime>());
        Assert.That(retry.MoveNext(), Is.False, "Stale timer must not perform any network request.");
    }

    [TestCase("clothing.skirt_primal_1")]
    [TestCase("clothing.dress_primal_color05")]
    [TestCase("underwear.briefs_primal_male_panty3")]
    public void ReportedGroundWearCanBeClassifiedBeforeItsPayloadArrives(string id)
    {
        Assert.That(GroundPileCatalog.IsGarment(id), Is.True);
    }

    private static ContentRecord Record(string type, string id) => new()
    { type = type, id = id, revision = 1, variant = new ContentVariant { sha256 = new string('a', 64), size = 1 } };
    private void Set(string name, object value) => typeof(ContentAssetService).GetField(name, Fields).SetValue(_service, value);
    private T Get<T>(string name) => (T)typeof(ContentAssetService).GetField(name, Fields).GetValue(_service);
    private object Invoke(string name, params object[] args) =>
        typeof(ContentAssetService).GetMethod(name, Fields).Invoke(_service, args);
}
}
