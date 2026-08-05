#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

// ---------------------------------------------------------------------------
//  Вынос арта вещей из Resources.
//
//  Пока папка лежит в Resources, она попадает в билд ЦЕЛИКОМ и БЕЗУСЛОВНО —
//  вместе со всеми мешами и текстурами, на которые ссылаются её префабы.
//  Разметка адресами этого не отменяет: Addressables добавляет второй путь
//  доставки, но не убирает первый.
//
//  Переезд делается через AssetDatabase, а не мышкой и не `mv`: так Unity
//  переносит .meta вместе с ассетом, GUID остаётся прежним — и уцелевают ВСЕ
//  ссылки разом (записи Addressables, вариации в GarmentDefinition, префабы,
//  сцены). GUID тут — единственное, что связывает вещь с её артом.
//
//  Menu: HexLive ▸ Addressables ▸ Вынести арт вещей из Resources
// ---------------------------------------------------------------------------
public static class HexLiveWearOutOfResources
{
    public const string OldRoot = "Assets/Resources/HexLive/Wear";
    public const string NewRoot = "Assets/HexLiveContent/Wear";

    [MenuItem("HexLive/Addressables/Вынести арт вещей из Resources")]
    public static void Move()
    {
        if (!AssetDatabase.IsValidFolder(OldRoot))
        {
            Debug.Log($"[Addressables] {OldRoot} уже нет — арт вынесен раньше.");
            return;
        }

        EnsureFolder(NewRoot);

        var moved = 0;
        var failed = new List<string>();
        var folders = AssetDatabase.GetSubFolders(OldRoot);

        AssetDatabase.StartAssetEditing();
        try
        {
            foreach (var folder in folders)
            {
                var name = Path.GetFileName(folder);
                var error = AssetDatabase.MoveAsset(folder, $"{NewRoot}/{name}");
                if (string.IsNullOrEmpty(error))
                {
                    moved++;
                }
                else
                {
                    failed.Add($"{name}: {error}");
                }
            }
        }
        finally
        {
            AssetDatabase.StopAssetEditing();
            AssetDatabase.Refresh();
        }

        if (failed.Count > 0)
        {
            Debug.LogError($"[Addressables] не переехали ({failed.Count}): {string.Join("; ", failed)}");
        }

        Debug.Log($"[Addressables] арт вещей вынесен из Resources: {moved} папок -> {NewRoot}.\n" +
                  "Дальше обязательно: «Разметить гардероб и волосы» (адреса строятся от нового пути) " +
                  "и проверить, что вещи одеваются — грузятся они теперь по адресу.");
    }

    private static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path))
        {
            return;
        }

        var parts = path.Split('/');
        var current = parts[0];
        for (var i = 1; i < parts.Length; i++)
        {
            var next = $"{current}/{parts[i]}";
            if (!AssetDatabase.IsValidFolder(next))
            {
                AssetDatabase.CreateFolder(current, parts[i]);
            }

            current = next;
        }
    }
}
#endif
