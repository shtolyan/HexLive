#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using FMODUnity;
using UnityEditor;
using UnityEngine;

// ---------------------------------------------------------------------------
//  Настройка FMOD на банки, которые лежат в РЕПОЗИТОРИИ.
//
//  Зачем это код, а не «сделай руками по вики». `FMODStudioSettings.asset`
//  живёт внутри `Assets/Plugins/FMOD/Resources/`, а вся эта папка —
//  297 МБ сторонних бинарников — под .gitignore. Значит настройки НЕ ездят с
//  проектом: каждая переустановка интеграции и каждая новая машина начинают с
//  чистого листа, и «почему нет звука» приходится вспоминать заново.
//
//  Договор проекта (CLAUDE.md §67.12): игра берёт ГОТОВЫЕ банки
//  `Assets/StreamingAssets/FMODBanks/*.bank`, которые закоммичены. Проект FMOD
//  Studio (`FMODStudio/HexLive/HexLive.fspro`) — источник правды для микса, но
//  его сборка `Build/` под ignore и на машине без FMOD Studio её нет вообще.
//  Поэтому Single Platform Build прямо на staged-банки, а не на .fspro:
//  указать .fspro на такой машине — значит попросить FMOD искать банки,
//  которых не существует.
//
//  Источник и приёмник копирования при этом СОВПАДАЮТ, и это безопасно
//  намеренно: `EventManager.CopyToStreamingAssets` сравнивает полные пути и
//  выходит РАНЬШЕ, чем чистит «устаревшие» файлы, — иначе шаг копирования
//  удалил бы закоммиченные банки.
//
//  Menu: HexLive ▸ FMOD ▸ Настроить на банки из репозитория
// ---------------------------------------------------------------------------
public static class HexLiveFmodSetup
{
    private const string BankFolderName = "FMODBanks";
    private const string SourceBankPath = "Assets/StreamingAssets/" + BankFolderName;
    private const string MasterBankName = "Master";

    [MenuItem("HexLive/FMOD/Настроить на банки из репозитория")]
    public static void Configure()
    {
        var report = Apply();
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
            Debug.Log(Apply());
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

    private static string Apply()
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

        // Рантайм собирает путь как streamingAssetsPath + TargetSubFolder, а
        // TargetSubFolder для StreamingAssets — это TargetBankFolder. Пустое
        // значение здесь означало бы «банки лежат в корне StreamingAssets», и
        // Master.bank не нашёлся бы.
        settings.ImportType = ImportType.StreamingAssets;
        settings.TargetBankFolder = BankFolderName;

        // §67.12: банки ГРУЗЯТСЯ, иначе события стартуют и умирают на нуле.
        settings.BankLoadType = BankLoadType.All;
        settings.AutomaticEventLoading = true;
        settings.HideSetupWizard = true;

        EditorUtility.SetDirty(settings);

        // Пересканировать банки ОБЯЗАТЕЛЬНО до сохранения: список
        // `MasterBanks` наполняет `EventManager.OnCacheChange`, а при
        // `BankLoadType.All` рантайм грузит ровно то, что в этом списке
        // (`RuntimeManager.BanksToLoad`). Сохранить ассет раньше — значит
        // записать пустой список и получить игру без единого звука, причём
        // без единой ошибки в консоли.
        EventManager.RefreshBanks();
        EditorUtility.SetDirty(settings);
        AssetDatabase.SaveAssets();

        if (settings.MasterBanks == null || settings.MasterBanks.Count == 0)
        {
            throw new InvalidOperationException(
                $"После пересканирования {SourceBankPath} список MasterBanks пуст — " +
                "при BankLoadType.All рантайм не загрузит ни одного банка и звука не будет.");
        }

        var total = banks.Sum(path => new FileInfo(path).Length);
        return $"[FMOD] настроено на {SourceBankPath}: банок {banks.Length} " +
               $"({total} байт), ImportType=StreamingAssets, TargetBankFolder={BankFolderName}, " +
               $"BankLoadType=All, MasterBanks=[{string.Join(", ", settings.MasterBanks)}], " +
               $"Banks=[{string.Join(", ", settings.Banks)}]. " +
               $"Версия интеграции 0x{FMOD.VERSION.number:x8}.";
    }
}
#endif
