#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using FMODUnity;
using UnityEditor;
using UnityEngine;

// ---------------------------------------------------------------------------
//  Настройка FMOD на банки, входящие в Player (§67/§152).
//
//  Зачем это код, а не «сделай руками по вики». `FMODStudioSettings.asset`
//  живёт внутри `Assets/Plugins/FMOD/Resources/`, а вся эта папка —
//  297 МБ сторонних бинарников — под .gitignore. Значит настройки НЕ ездят с
//  проектом: каждая переустановка интеграции и каждая новая машина начинают с
//  чистого листа, и «почему нет звука» приходится вспоминать заново.
//
//  Банки лежат в StreamingAssets/FMODBanks рядом с общими SFX/голосами.
//  Runtime грузит их локально при старте; сеть и live-content registry звуку
//  не нужны.
//
//  Menu: HexLive ▸ FMOD ▸ Настроить на банки из репозитория
// ---------------------------------------------------------------------------
public static class HexLiveFmodSetup
{
    private const string SourceBankPath = "Assets/StreamingAssets/FMODBanks";
    private const string MasterBankName = "Master";

    [MenuItem("HexLive/FMOD/Настроить на банки из репозитория")]
    public static void Configure()
    {
        var report = ConfigureAtomicPlayer();
        Debug.Log(report);
    }

    /// <summary>
    /// Доложить нативные библиотеки из `staging` в `platforms`.
    ///
    /// Свежий пакет FMOD привозит logging-варианты (`fmodstudioL.*`) в папке
    /// `staging`, и пока она на месте, `EventManager.UpdateCache` выходит ПЕРВОЙ
    /// же строкой — банки не сканируются, `MasterBanks` остаётся пустым, игра
    /// молчит. `StagingSystem.Startup()` для свежей установки сам выполняет
    /// копирование и убирает `staging`.
    ///
    /// Отдельный batch-вход, потому что шаг заканчивается
    /// `EditorUtility.RequestScriptReload()`: настраивать банки в том же
    /// процессе — значит работать поверх перезагружаемого домена.
    /// </summary>
    public static void StageLibrariesBatch()
    {
        var exitCode = 1;
        try
        {
            if (!StagingSystem.SourceLibsExist)
            {
                Debug.Log("[FMOD] staging пуст — нативные библиотеки уже на местах.");
            }
            else
            {
                var step = StagingSystem.Startup();
                if (step != null)
                {
                    // Ненулевой шаг = сценарий ОБНОВЛЕНИЯ поверх старых библиотек:
                    // он требует отключить их и перезапустить редактор. На чистой
                    // установке сюда не попадают, и молча продолжать нельзя.
                    throw new InvalidOperationException(
                        $"FMOD требует ручного шага обновления библиотек: «{step.Name}». " +
                        "Открой редактор и пройди FMOD ▸ Update Libraries.");
                }

                Debug.Log("[FMOD] нативные библиотеки разложены из staging.");
            }

            AssetDatabase.SaveAssets();
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

    /// <summary>
    /// Batch-mode вход: тот же результат, но с кодом выхода, чтобы скрипт
    /// сборки не поехал дальше с молчаливой игрой.
    /// </summary>
    public static void ConfigureBatch()
    {
        var exitCode = 1;
        try
        {
            Debug.Log(ConfigureAtomicPlayer());
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

    /// <summary>
    /// Applies the build-safe FMOD mode: banks are already in StreamingAssets,
    /// so FMOD indexes and auto-loads them without generating bundle stubs.
    /// </summary>
    public static string ConfigureAtomicPlayer()
    {
        var bankDirectory = Path.Combine(
            Path.GetDirectoryName(Application.dataPath) ?? ".", SourceBankPath);
        var banks = Directory.Exists(bankDirectory)
            ? Directory.GetFiles(bankDirectory, "*.bank")
            : Array.Empty<string>();
        if (banks.Length == 0)
        {
            throw new FileNotFoundException(
                $"Нет собранных банок в {SourceBankPath} — настраивать не на что.", bankDirectory);
        }

        if (!banks.Any(path =>
                string.Equals(Path.GetFileNameWithoutExtension(path), MasterBankName,
                    StringComparison.OrdinalIgnoreCase)))
        {
            throw new FileNotFoundException(
                $"В {SourceBankPath} нет {MasterBankName}.bank — без мастер-банка FMOD не стартует.",
                bankDirectory);
        }

        // Пока `staging` на месте, сканирование банок выходит первой строкой и
        // молча оставляет пустой кэш. Ловим это здесь, а не в звуке игры.
        if (StagingSystem.SourceLibsExist)
        {
            throw new InvalidOperationException(
                "В Assets/Plugins/FMOD/staging остались нативные библиотеки: пока они там, " +
                "EventManager не сканирует банки. Сначала HexLiveFmodSetup.StageLibrariesBatch.");
        }

        var settings = Settings.Instance;
        if (settings == null)
        {
            throw new InvalidOperationException("FMODUnity.Settings.Instance == null.");
        }

        // Готовые банки, без проекта Studio и без деления по платформам.
        settings.HasSourceProject = false;
        settings.HasPlatforms = false;
        settings.SourceProjectPath = string.Empty;
        settings.SourceBankPath = SourceBankPath;

        settings.ImportType = ImportType.StreamingAssets;
        settings.TargetBankFolder = "FMODBanks";
        settings.BankLoadType = BankLoadType.All;
        settings.AutomaticEventLoading = true;
        settings.AutomaticSampleLoading = false;
        settings.HideSetupWizard = true;
        settings.MasterBanks?.Clear();
        settings.MasterBanks?.Add(MasterBankName);
        settings.Banks?.Clear();
        settings.BanksToLoad?.Clear();
        EditorUtility.SetDirty(settings);
        AssetDatabase.SaveAssets();

        var total = banks.Sum(path => new FileInfo(path).Length);
        return $"[FMOD] настроено на {SourceBankPath}: банок {banks.Length} " +
               $"({total} байт), Player StreamingAssets, BankLoadType=All. " +
               $"Версия интеграции 0x{FMOD.VERSION.number:x8}.";
    }
}
#endif
