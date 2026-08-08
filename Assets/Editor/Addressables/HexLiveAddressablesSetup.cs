#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEngine;

// ---------------------------------------------------------------------------
//  Addressables для HexLive: контент живёт РЯДОМ с игрой, а не внутри неё.
//
//  Зачем. Всё, на что ссылается сцена или Resources, попадает в билд — а на
//  гардероб и волосы завязаны гигабайты текстур и мешей. Поэтому каждая сборка
//  игры перемалывала весь контент, даже когда менялась одна строка кода.
//
//  Как устроено. Две переменные профиля:
//    * HexLiveContent.BuildPath — куда Addressables КЛАДЁТ собранный контент
//      (внутри проекта, в Build/, не в Assets — иначе Unity его импортирует);
//    * HexLiveContent.LoadPath  — каталоговый (legacy) адрес контента. На
//      Windows/Linux `Application.dataPath/..` уже означает папку рядом с exe;
//      на macOS это попало бы внутрь .app, поэтому рантайм
//      ExternalContentPath переписывает его на общую HexLiveContent рядом с
//      .app. Старые каталоги и бандлы благодаря этому не пересобираются.
//
//  ⭐ В РЕДАКТОРЕ бандлы не нужны вовсе. Режим воспроизведения ставится в
//  «Use Asset Database»: игра берёт ассеты напрямую из проекта, правка видна
//  сразу, собирать контент не надо. Собранный контент нужен только плееру —
//  поэтому сборка контента и сборка игры разведены и делаются по отдельности.
//
//  Menu: HexLive ▸ Addressables ▸ *
// ---------------------------------------------------------------------------
public static class HexLiveAddressablesSetup
{
    public const string BuildPathVariable = "HexLiveContent.BuildPath";
    public const string LoadPathVariable = "HexLiveContent.LoadPath";

    // Внутри проекта, но ВНЕ Assets: собранные бандлы Unity импортировать не
    // должен, иначе они же поедут и в билд — ровно то, от чего уходим.
    public const string BuildPathValue = "Build/AddressableContent/[BuildTarget]";

    // Каталоговый адрес. На macOS ExternalContentPath переносит его из .app в
    // общую папку рядом с приложением; строку сохраняем ради совместимости с
    // уже собранным каталогом.
    public const string ContentFolderName = "HexLiveContent";
    public const string LoadPathValue =
        "{UnityEngine.Application.dataPath}/../" + ContentFolderName + "/[BuildTarget]";

    [MenuItem("HexLive/Addressables/Настроить проект")]
    public static void Configure()
    {
        var settings = AddressableAssetSettingsDefaultObject.GetSettings(true);
        if (settings == null)
        {
            Debug.LogError("[Addressables] не удалось создать настройки.");
            return;
        }

        var profiles = settings.profileSettings;
        var profileId = settings.activeProfileId;

        profiles.CreateValue(BuildPathVariable, BuildPathValue);
        profiles.CreateValue(LoadPathVariable, LoadPathValue);
        profiles.SetValue(profileId, BuildPathVariable, BuildPathValue);
        profiles.SetValue(profileId, LoadPathVariable, LoadPathValue);

        // Каталог тоже уезжает наружу: он часть контента, а не часть игры.
        // Иначе игра носила бы в себе список адресов, который устаревает при
        // первой же пересборке одного гардероба.
        settings.BuildRemoteCatalog = true;
        settings.RemoteCatalogBuildPath.SetVariableByName(settings, BuildPathVariable);
        settings.RemoteCatalogLoadPath.SetVariableByName(settings, LoadPathVariable);

        // Player Build must only package the executable. Value 0 means
        // "use the global Editor preference" (which defaults to ON), not OFF;
        // keep this explicit so a normal build never spends tens of minutes
        // rebuilding the external wardrobe bundles.
        settings.BuildAddressablesWithPlayerBuild =
            AddressableAssetSettings.PlayerBuildOption.DoNotBuildWithPlayer;

        foreach (var group in settings.groups)
        {
            if (group == null || group.ReadOnly)
            {
                continue;
            }

            PointGroupOutside(settings, group);
        }

        EditorUtility.SetDirty(settings);
        AssetDatabase.SaveAssets();

        Debug.Log($"[Addressables] профиль настроен.\n" +
                  $"  сборка контента -> {BuildPathValue}\n" +
                  $"  игра берёт из   -> {LoadPathValue}\n" +
                  "В редакторе бандлы не нужны: Play Mode Script = Use Asset Database.");
    }

    // Одна группа: куда класть и откуда грузить. Отдельным методом, потому что
    // это же нужно каждой НОВОЙ группе (гардероб, волосы, …).
    public static void PointGroupOutside(AddressableAssetSettings settings, AddressableAssetGroup group)
    {
        var schema = group.GetSchema<BundledAssetGroupSchema>();
        if (schema == null)
        {
            return;
        }

        schema.BuildPath.SetVariableByName(settings, BuildPathVariable);
        schema.LoadPath.SetVariableByName(settings, LoadPathVariable);
        EditorUtility.SetDirty(schema);
    }

    [MenuItem("HexLive/Addressables/Собрать контент")]
    public static void BuildContent()
    {
        var settings = AddressableAssetSettingsDefaultObject.Settings;
        if (settings == null)
        {
            Debug.LogError("[Addressables] сначала «Настроить проект».");
            return;
        }

        AddressableAssetSettings.BuildPlayerContent(out var result);
        if (!string.IsNullOrEmpty(result.Error))
        {
            Debug.LogError($"[Addressables] сборка контента ПРОВАЛИЛАСЬ: {result.Error}");
            return;
        }

        var built = BuiltContentFolder();

        // Мета-файлы пишутся СРАЗУ после сборки и рядом с бандлами: игра должна
        // уметь решить «эти трусы подходят стартовой девушке», не открывая
        // бандл, а слой и зоны покрытия лежат именно в мете.
        HexLiveBundleMeta.WriteBeside(built);
        Debug.Log($"[Addressables] контент собран за {result.Duration:F1} с -> {built}\n" +
                  "Игра его НЕ несёт: положи эту папку рядом с exe под именем " +
                  $"«{ContentFolderName}» (меню «Положить рядом с игрой» делает это само).");
    }

    // Копия в папку игры. Билд игры и билд контента живут отдельно — поэтому
    // копирование ОТДЕЛЬНОЕ действие, а не хвост сборки: поменял одежду —
    // пересобрал контент и положил, exe не трогал.
    [MenuItem("HexLive/Addressables/Положить рядом с игрой…")]
    public static void DeployBesideGame()
    {
        var built = BuiltContentFolder();
        if (!Directory.Exists(built))
        {
            Debug.LogError($"[Addressables] нечего класть: {built} не существует — сначала «Собрать контент».");
            return;
        }

        // Диалог здесь УМЕСТЕН: его открывает человек, руками, и он же на него
        // отвечает. Запрет из CLAUDE.md — про модалки на автоматическом пути.
        var target = EditorUtility.SaveFolderPanel("Папка игры (рядом с exe)", "", "");
        if (string.IsNullOrEmpty(target))
        {
            return;
        }

        var destination = Path.Combine(target, ContentFolderName);
        CopyTree(Path.GetDirectoryName(built), destination);
        Debug.Log($"[Addressables] контент положен: {destination}");
    }

    private static string BuiltContentFolder()
    {
        var root = Path.GetDirectoryName(Application.dataPath);
        return Path.Combine(root ?? ".", "Build", "AddressableContent",
            EditorUserBuildSettings.activeBuildTarget.ToString());
    }

    private static void CopyTree(string from, string to)
    {
        if (Directory.Exists(to))
        {
            Directory.Delete(to, true);
        }

        foreach (var dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(dir.Replace(from, to));
        }

        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, file.Replace(from, to), true);
        }
    }
}
#endif
