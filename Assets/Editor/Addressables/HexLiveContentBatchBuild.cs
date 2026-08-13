#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEngine;

// ---------------------------------------------------------------------------
//  Batch-mode вход для сборки внешнего Addressables-контента.
//
//  Меню «HexLive ▸ Addressables ▸ Собрать контент» — для человека в редакторе:
//  оно логирует ошибку и живёт дальше. Скрипту сборки этого мало: процесс
//  обязан завершиться ненулевым кодом, если контент не собрался, иначе билд
//  игры поедет с пустой или устаревшей папкой бандлов и упадёт молча уже у
//  игрока. Поэтому отдельный вход, который строит контент, пишет меты и
//  отвечает кодом выхода.
//
//  Запуск (из Tools/build_release_windows.py, работает для любого таргета):
//    Unity -batchmode -quit -buildTarget <target>
//          -executeMethod HexLiveContentBatchBuild.Build
// ---------------------------------------------------------------------------
public static class HexLiveContentBatchBuild
{
    public static void Build()
    {
        var exitCode = 1;
        try
        {
            if (!Application.isBatchMode)
            {
                throw new InvalidOperationException(
                    "HexLiveContentBatchBuild is a batch-mode entry point. " +
                    "In the editor use HexLive ▸ Addressables ▸ Собрать контент.");
            }

            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null)
            {
                throw new InvalidOperationException(
                    "Addressables settings are missing; run HexLive ▸ Addressables ▸ Настроить проект first.");
            }

            AddressableAssetSettings.BuildPlayerContent(out var result);
            if (!string.IsNullOrEmpty(result.Error))
            {
                throw new InvalidOperationException($"Addressables content build failed: {result.Error}");
            }

            var root = Path.GetDirectoryName(Application.dataPath);
            var built = Path.Combine(root ?? ".", "Build", "AddressableContent",
                EditorUserBuildSettings.activeBuildTarget.ToString());
            if (!Directory.Exists(built))
            {
                throw new InvalidOperationException(
                    $"Addressables build reported success but produced no folder: {built}");
            }

            // Меты — часть контента (см. HexLiveBundleMeta): без них игра не
            // может решать «подходит ли вещь», не открывая бандл.
            var metas = HexLiveBundleMeta.WriteBeside(built);
            if (metas == 0)
            {
                throw new InvalidOperationException(
                    $"No bundle meta files were written beside {built}; wear bundles are missing?");
            }

            Debug.Log($"HexLive content batch build succeeded: {built} " +
                      $"({result.Duration:F1}s, {metas} meta files).");
            exitCode = 0;
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
        }
        finally
        {
            EditorApplication.Exit(exitCode);
        }
    }
}
#endif
