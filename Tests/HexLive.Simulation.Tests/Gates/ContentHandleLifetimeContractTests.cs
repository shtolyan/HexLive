using System;
using System.IO;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates
{

/// <summary>
/// §152: время жизни хэндлов контента и бандлов под ними (баг #241 — редкие
/// вылеты на Windows). Гейт текстовый, потому что весь код живёт в
/// UnityPresentation и headless-сборкой не проверяется; раньше он назывался
/// Addressable* и после переезда на ContentAssetService сторожил строки,
/// которых в проекте больше нет.
/// </summary>
public sealed class ContentHandleLifetimeContractTests
{
    private static readonly string[] HandleCaches =
    {
        Path.Combine("Wearing", "HairContent.cs"),
        Path.Combine("Wearing", "ActorWardrobe.cs"),
        Path.Combine("Wearing", "ProstheticContent.cs"),
        Path.Combine("Wearing", "Garments", "ItemIcons.cs"),
        Path.Combine("Content", "ContentPrefabCache.cs"),
        Path.Combine("Content", "AtomicResources.cs"),
    };

    [Test]
    public void HandleCachesResetAndDisposeOnSubsystemRegistration()
    {
        Assert.Multiple(() =>
        {
            foreach (var relative in HandleCaches)
            {
                var source = Presentation(relative);
                Assert.That(source, Does.Contain("RuntimeInitializeLoadType.SubsystemRegistration"),
                    relative + ": статический кэш хэндлов не сбрасывается на старте");
                Assert.That(source, Does.Contain("Dispose()"),
                    relative + ": хэндлы не освобождаются при сбросе");
                Assert.That(source, Does.Contain(".Clear();"),
                    relative + ": словарь хэндлов не чистится при сбросе");
            }
        });
    }

    [Test]
    public void ServiceUnloadsEveryBundleWhenStaticsReset()
    {
        var source = Presentation(Path.Combine("Content", "ContentAssetService.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain("RuntimeInitializeLoadType.SubsystemRegistration"));
            Assert.That(source, Does.Contain("bundle.Bundle != null"),
                "reset должен использовать Unity null-check перед Unload");
            Assert.That(source, Does.Contain("bundle.Bundle.Unload(true);"),
                "бандлы прошлой сессии обязаны выгружаться вместе со своими ассетами");
            Assert.That(source, Does.Contain("_instance = null;"));
        });
    }

    /// <summary>
    /// Бандл держится с момента запроса ассета, а не с момента его получения.
    /// Иначе Release чужого хэндла успевает выгрузить бандл из-под живого
    /// AssetBundleRequest — на Windows это падение в нативном коде.
    /// </summary>
    [Test]
    public void BundleIsRetainedBeforeTheAsyncAssetRequestStarts()
    {
        var source = Presentation(Path.Combine("Content", "ContentAssetService.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(BeforeRequest(source, "bundle.LoadAssetAsync<T>(assetEntry)"),
                Does.Contain("Retain(loadedSha);"),
                "LoadAsset: Retain обязан стоять до LoadAssetAsync");
            Assert.That(BeforeRequest(source, "bundle.LoadAllAssetsAsync<T>()"),
                Does.Contain("Retain(loadedSha);"),
                "LoadAllAssets: Retain обязан стоять до LoadAllAssetsAsync");
            Assert.That(AfterRequest(source, "bundle.LoadAssetAsync<T>(assetEntry)"),
                Does.Contain("Release(loadedSha);"),
                "LoadAsset: страховочный Retain обязан сниматься, когда ассета нет");
            Assert.That(AfterRequest(source, "bundle.LoadAllAssetsAsync<T>()"),
                Does.Contain("Release(loadedSha);"),
                "LoadAllAssets: страховочный Retain обязан сниматься после выдачи хэндлов");
        });
    }

    /// <summary>
    /// Чистка кэша не трогает блоб, который прямо сейчас открыт Unity или
    /// докачивается: на Windows File.Delete по такому файлу бросает sharing
    /// violation (на POSIX unlink проходит молча), а исключение отсюда убивало
    /// корутину загрузки вместе с ContentQueue.End.
    /// </summary>
    [Test]
    public void CacheTrimSkipsBusyBlobsAndSurvivesADeniedDelete()
    {
        var source = Presentation(Path.Combine("Content", "ContentAssetService.cs"));
        var trim = Between(source, "private void TrimCache()", "private string BlobPath(");

        Assert.Multiple(() =>
        {
            Assert.That(trim, Does.Contain("protectedHashes.UnionWith(_bundles.Keys);"),
                "загружающийся бандл тоже держит файл, не только уже открытый");
            Assert.That(trim, Does.Contain("protectedHashes.UnionWith(_blobRequests.Keys);"),
                "файл, в который идёт докачка, удалять нельзя");
            Assert.That(trim, Does.Contain("try"), "file.Delete() обязан быть под try/catch");
            Assert.That(trim, Does.Contain("catch (Exception"),
                "отказ удаления не должен ронять корутину загрузки");
            Assert.That(trim, Does.Contain("Debug.LogWarning"),
                "занятый блоб обязан быть виден в логе Player-а");
        });
    }

    /// <summary>
    /// §152 закрыл Addressables. Гейт, сторожащий несуществующее API, врёт
    /// молча — поэтому путь контента проверяется на их отсутствие.
    /// </summary>
    [Test]
    public void PresentationContentPathHasNoAddressablesLeftovers()
    {
        Assert.Multiple(() =>
        {
            foreach (var relative in HandleCaches)
            {
                var source = Presentation(relative);
                Assert.That(source, Does.Not.Contain("AsyncOperationHandle"), relative);
                Assert.That(source, Does.Not.Contain("UnityEngine.AddressableAssets"), relative);
            }
        });
    }

    private static string Presentation(string relative)
    {
        var path = Path.Combine(
            RepoPaths.Root, "Assets", "HexLive", "UnityPresentation", relative);
        Assert.That(File.Exists(path), Is.True, "нет файла " + path);
        return File.ReadAllText(path);
    }

    private static string BeforeRequest(string source, string request)
    {
        var call = Index(source, request);
        var scope = source.LastIndexOf("LoadBundleWithFallback(record", call, StringComparison.Ordinal);
        Assert.That(scope, Is.GreaterThanOrEqualTo(0),
            "не найден вызов LoadBundleWithFallback перед " + request);
        return source[scope..call];
    }

    private static string AfterRequest(string source, string request)
    {
        var call = Index(source, request);
        var end = source.IndexOf("completed", call, StringComparison.Ordinal);
        Assert.That(end, Is.GreaterThan(call), "не найдено завершение запроса " + request);
        var tail = source.IndexOf("};", end, StringComparison.Ordinal);
        return source[call..(tail < 0 ? source.Length : tail)];
    }

    private static string Between(string source, string from, string to)
    {
        var start = Index(source, from);
        var end = source.IndexOf(to, start, StringComparison.Ordinal);
        return source[start..(end < 0 ? source.Length : end)];
    }

    private static int Index(string source, string needle)
    {
        var index = source.IndexOf(needle, StringComparison.Ordinal);
        Assert.That(index, Is.GreaterThanOrEqualTo(0), "не найдено: " + needle);
        return index;
    }
}

}
