using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using HexLive.UnityPresentation.Content;
using HexLive.UnityPresentation.Wearing.Garments;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace HexLive.Tests
{
// Exercise actual icon/service callbacks at the download-completion boundary.
// The pending transport never reads files or sends requests to any endpoint.
public sealed class ItemIconRecoveryRuntimeTests
{
    private const BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.NonPublic;
    private const string Id = "tool.torch";
    private ContentAssetService _service;
    private object _previous;
    private ContentRecord _record;
    private List<Action<bool>> _pending;

    [SetUp]
    public void SetUp()
    {
        ResetIcons();
        _service = (ContentAssetService)FormatterServices.GetUninitializedObject(typeof(ContentAssetService));
        _record = new ContentRecord { type = "object", id = Id, revision = 1,
            variant = new ContentVariant { sha256 = new string('a', 64), size = 1,
                payloadType = "assetBundle", entryAsset = "main", iconAsset = "icon" } };
        Set("_pinned", new Dictionary<string, ContentRecord> { [_record.Key] = _record });
        Set("_verified", new Dictionary<string, ContentRecord>());
        Set("_previous", new Dictionary<string, ContentRecord>());
        Set("_settledBlobs", new HashSet<string>());
        Set("_registryWaiters", new List<Action>());
        Set("_registryReady", true);
        Set("<LastError>k__BackingField", string.Empty);
        var field = typeof(ContentAssetService).GetField("_blobRequests", InstanceFields);
        var requests = (IDictionary)Activator.CreateInstance(field.FieldType);
        var pendingType = typeof(ContentAssetService).GetNestedType("BlobState", BindingFlags.NonPublic);
        var pending = Activator.CreateInstance(pendingType, true);
        _pending = (List<Action<bool>>)pendingType.GetField("Waiters").GetValue(pending);
        requests.Add(_record.variant.sha256, pending);
        field.SetValue(_service, requests);
        var singleton = typeof(ContentAssetService).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic);
        _previous = singleton.GetValue(null);
        singleton.SetValue(null, _service);
        ContentQueue.Reset();
    }

    [TearDown]
    public void TearDown()
    {
        ResetIcons();
        typeof(ContentAssetService).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, _previous);
        ContentQueue.Reset();
    }

    [Test]
    public void RefreshedRegistryLoadsAnIconWhichUsedToBeAbsent()
    {
        _record.variant.iconAsset = null;
        Assert.That(ItemIcons.Load(Id), Is.Null);
        Assert.That(_pending, Is.Empty);
        _record.variant.iconAsset = "icon";
        typeof(ContentAssetService).GetMethod("CompleteRegistry", InstanceFields).Invoke(_service, null);
        ItemIcons.Load(Id);
        Assert.That(_pending, Has.Count.EqualTo(1), "Publishing an icon must not require restarting the game.");
    }

    [UnityTest]
    public IEnumerator FailedDownloadRetriesAfterDelayWithoutReopeningTheWindow()
    {
        ItemIcons.Load(Id);
        Assert.That(_pending, Has.Count.EqualTo(1));
        FailDownload();
        Assert.That(ContentQueue.IsIdle, Is.True, "A failed icon must not keep the loading curtain open.");
        for (var i = 0; i < 16; i++) ItemIcons.Load(Id);
        Assert.That(_pending, Is.Empty, "UI refreshes must not hammer the failed download.");
        yield return new WaitForSecondsRealtime(4.1f);
        ItemIcons.Load(Id);
        Assert.That(_pending, Has.Count.EqualTo(1), "A transient error must not become permanent emoji fallback.");
    }

    [Test]
    public void OpeningTheOwnerCanRecoverItsIconImmediately()
    {
        ItemIcons.Load(Id);
        FailDownload();
        ItemIcons.PrewarmOwner("object", Id);
        Assert.That(_pending, Has.Count.EqualTo(1), "An opened model bundle already has the icon's bytes.");
    }

    [Test]
    public void AnOwnerWithoutAnAuthoredIconKeepsCheapEmojiFallback()
    {
        _record.variant.iconAsset = null;
        for (var i = 0; i < 16; i++) ItemIcons.Load(Id);
        Assert.That(_pending, Is.Empty);
        Assert.That(ItemIcons.Revision, Is.EqualTo(1));
        Assert.That(ContentQueue.IsIdle, Is.True);
    }

    [Test]
    public void OfflineRegistryRefreshDoesNotBypassDownloadBackoff()
    {
        ItemIcons.Load(Id);
        FailDownload();
        Set("<LastError>k__BackingField", "Registry offline");
        typeof(ContentAssetService).GetMethod("CompleteRegistry", InstanceFields).Invoke(_service, null);
        ItemIcons.Load(Id);
        Assert.That(_pending, Is.Empty, "A degraded registry must not trigger a request storm.");
    }

    private void FailDownload()
    {
        var callbacks = _pending.ToArray();
        _pending.Clear();
        foreach (var callback in callbacks) callback(false);
    }
    private void Set(string name, object value) => typeof(ContentAssetService).GetField(name, InstanceFields).SetValue(_service, value);
    private static void ResetIcons() => typeof(ItemIcons).GetMethod("ResetStatics", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
}
}
