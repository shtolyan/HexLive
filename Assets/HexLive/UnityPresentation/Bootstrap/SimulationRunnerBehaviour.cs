#nullable enable
using System.Collections.Generic;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Debug;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Wire;
using HexLive.UnityPresentation.History;
using UnityEngine;
using EntityId = HexLive.Simulation.Common.EntityId;

namespace HexLive.UnityPresentation.Bootstrap
{

/// <summary>
/// The scene's single handle on the simulation, and the only MonoBehaviour that
/// knows one exists.
/// <para>
/// It owns no world of its own: it holds an <see cref="ISimulationBackend"/> and
/// forwards to it. Today that is always <see cref="LocalEngineBackend"/> — the
/// world runs in this process exactly as it always has. A networked backend
/// slots in at the same place without touching the sixteen components that fetch
/// this behaviour with <c>FindAnyObjectByType</c> (which cannot be given an
/// interface — Unity requires a UnityEngine.Object).
/// </para>
/// <para>
/// What stays HERE rather than in the backend is everything that is presentation
/// even in single-player: the colony history log, fanning events out to sound and
/// the console, autosave cadence, and the two watchdogs that catch a loading
/// screen dying mid-curtain.
/// </para>
/// <para>
/// §83: runs BEFORE default-order scripts. The renderer reads
/// <see cref="TickAlpha"/> and the backend-mutated snapshot in its own Update;
/// without an explicit order Unity may call it first, showing last frame's
/// alpha against this frame's snapshot — a one-frame hitch that comes and goes
/// with script reload order.
/// </para>
/// </summary>
[DefaultExecutionOrder(-100)]
public sealed class SimulationRunnerBehaviour : MonoBehaviour, ISimulationSource, IAdminSimulationSource
{
    [SerializeField] private WorldBootstrapAsset? _bootstrapAsset;
    [SerializeField] private bool _startPaused = true;
    [SerializeField] private float _initialSpeed = 1f;

    // Perf: mirroring EVERY sim trace event into Debug.Log was an editor
    // killer — each entry captures a stack trace (frame cost at 50x speed)
    // and the console/Editor.log accumulate across play sessions, so the
    // editor got slower with every run. The debug panel reads the event
    // buffer directly; keep the console on high-signal events by default and
    // flip full trace on only when deep console tracing is needed.
    [SerializeField] private bool _logImportantEventsToConsole = true;
    [SerializeField] private bool _logTraceEventsToConsole;

    private ISimulationBackend? _backend;
    private long _lastLoggedSeq;
    private readonly List<SimulationEvent> _drainedEvents = new();
    private readonly GameHistoryLog _gameHistory = new();

    /// <summary>§41.3: экран загрузки берёт прогрев арта СЕБЕ, чтобы крутить
    /// его параллельно намотке офлайна — иначе восемь мегабайт префабов и все
    /// сэмплы FMOD читаются с диска ДО первого тика, и намотка ждёт их зря.
    /// Все остальные вызывающие (dev-сцены, мир из JSON) ничего не замечают:
    /// при выключенном флаге <see cref="Bootstrap(WorldBootstrapDefinition)"/>
    /// греет всё сам, ровно как раньше.</summary>
    public static bool DeferContentPrewarm { get; set; }

    private bool _contentWarmed;
    private bool _presentationPaused;
    private float _timeScaleBeforePause = 1f;

    /// <summary>
    /// The live engine — LOCAL MODE ONLY, null otherwise.
    /// <para>
    /// This is the dev/test-scene escape hatch: AmputationTest, BedBuildTest,
    /// SwimTest and WolfFightTest build throwaway worlds and mutate them
    /// directly, which no interface should make comfortable. Shipped
    /// presentation must read <see cref="ISimulationSource"/> instead — a new
    /// <c>Engine.World</c> reference in a UI or rendering file is a bug that
    /// only shows up the day the world stops being local.
    /// </para>
    /// </summary>
    public SimulationEngine? Engine => (_backend as LocalEngineBackend)?.Engine;

    public GameHistoryLog GameHistory => _gameHistory;

    public bool IsReady => _backend?.IsReady ?? false;

    public SimulationLink Link => _backend?.Link ?? SimulationLink.Local;

    public bool IsCompleted => _backend?.IsCompleted ?? false;

    public int Seed => _backend?.Seed ?? 0;

    public int CurrentTick => _backend?.CurrentTick ?? 0;

    public bool IsPaused => _backend?.IsPaused ?? true;

    public float SpeedMultiplier => _backend?.SpeedMultiplier ?? 1f;

    /// <summary>
    /// Fraction [0..1) of progress toward the next simulation tick.
    /// Used by renderers to interpolate between discrete tick states.
    /// </summary>
    public float TickAlpha => _backend?.TickAlpha ?? 0f;

    public bool SupportsDirectWorldMutation => _backend?.SupportsDirectWorldMutation ?? false;

    public bool SupportsClientSave => _backend?.SupportsClientSave ?? false;

    public bool SupportsNpcCommands => _backend?.SupportsNpcCommands ?? false;

    public bool CanControlNpc(EntityId npc) => _backend?.CanControlNpc(npc) ?? false;
    // Ownership grants perception even while an MCP attachment blocks manual commands.
    public bool IsAssignedNpc(EntityId npc) => _backend is Remote.RemoteSocketBackend remote
        ? remote.IsAssignedNpc(npc) : CanControlNpc(npc);

    public bool SupportsAgentIntegration => _backend?.SupportsAgentIntegration ?? false;

    public bool SttAvailable => _backend?.SttAvailable ?? false;

    public bool TryGetAgentState(EntityId npc, out AgentStateFrame state)
    {
        if (_backend != null) return _backend.TryGetAgentState(npc, out state);
        state = new AgentStateFrame { NpcId = npc.Value };
        return false;
    }

    public void RequestSttToken(int correlationId) => _backend?.RequestSttToken(correlationId);

    public bool TryTakeSttTokenResult(out SttTokenResultFrame result)
    {
        if (_backend != null) return _backend.TryTakeSttTokenResult(out result);
        result = new SttTokenResultFrame();
        return false;
    }

    public void SendAgentText(int correlationId, EntityId npc, string messageId,
        string language, string text) =>
        _backend?.SendAgentText(correlationId, npc, messageId, language, text);

    public bool TryTakeAgentTextResult(out AgentTextResultFrame result)
    {
        if (_backend != null) return _backend.TryTakeAgentTextResult(out result);
        result = new AgentTextResultFrame();
        return false;
    }

    public bool TryTakeAgentSpeech(out AgentSpeechMessage speech)
    {
        if (_backend != null) return _backend.TryTakeAgentSpeech(out speech);
        speech = null!;
        return false;
    }

    public bool TryGetCraftingOptions(EntityId npc, List<CraftRecipeOption> into)
    {
        if (_backend is not null)
        {
            return _backend.TryGetCraftingOptions(npc, into);
        }

        into.Clear();
        return false;
    }

    public void EnqueueCommand(ISimulationCommand command) => _backend?.EnqueueCommand(command);

    /// <summary>§41.3: пока идёт намотка офлайна, мир принадлежит воркеру, и
    /// экспорт снапшота — обход КАЖДОЙ сущности — споткнулся бы о правку на
    /// полушаге. Отказ ровно здесь закрывает вопрос для всех потребителей
    /// сразу: снапшот и так бывает null (до Configure мира ещё нет), и это все
    /// уже умеют. Гейт по IsReplaying в каждой панели пришлось бы помнить в
    /// каждой новой панели.</summary>
    public WorldSnapshot? CreateSnapshot() =>
        UI.LoadingScreen.IsReplaying ? null : _backend?.CreateSnapshot();

    public bool TryGetObjectDefinition(string id, out ObjectDefinition? definition)
    {
        if (_backend is not null)
        {
            return _backend.TryGetObjectDefinition(id, out definition);
        }

        definition = null;
        return false;
    }

    public long DrainEvents(long sinceSeq, List<SimulationEvent> into) =>
        _backend?.DrainEvents(sinceSeq, into) ?? sinceSeq;

    /// <summary>§30.17: -hexlive-trace — «мне нужна диагностика с первого
    /// тика» (иначе её включают тумблером панели уже в игре).</summary>
    private static bool TraceRequestedOnCommandLine()
    {
        try
        {
            foreach (var arg in System.Environment.GetCommandLineArgs())
            {
                if (string.Equals(arg, "-hexlive-trace", System.StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch (System.Exception)
        {
            // Платформа не отдаёт командную строку — молчим, как и по умолчанию.
        }

        return false;
    }

    public string AdminClientId => (_backend as IAdminSimulationSource)?.AdminClientId ?? string.Empty;
    public void SendAgentPairing(string id, string code, bool approve) =>
        (_backend as Remote.RemoteSocketBackend)?.SendAgentPairing(id, code, approve);
    public bool TryTakeAgentPairing(out (string Id, string Text, bool Approved) result)
    {
        if (_backend is Remote.RemoteSocketBackend remote) return remote.TryTakeAgentPairing(out result);
        result = default; return false;
    }
    public string AdminServer => (_backend as IAdminSimulationSource)?.AdminServer ?? string.Empty;
    public void SendAdmin(string json) => (_backend as IAdminSimulationSource)?.SendAdmin(json);
    public bool TryTakeAdminResult(out string json)
    {
        if (_backend is IAdminSimulationSource admin) return admin.TryTakeAdminResult(out json);
        json = string.Empty; return false;
    }

    private void Awake()
    {
        SimulationSource.Current = this;
        if (GetComponent<UI.AdminVoicePanel>() == null) gameObject.AddComponent<UI.AdminVoicePanel>();
        if (_backend is null)
        {
            Bootstrap();
        }
    }

    // Dead-world watchdog: with no world JSON, SOMETHING must call Configure
    // shortly after startup (normally the LoadingScreen, in dev scenes their
    // test bootstrap). If nothing has after a grace period and there is no
    // LoadingScreen around to eventually do it, the game would just sit on a
    // black screen forever — that is an ERROR, not a warning.
    private const float UnconfiguredErrorAfterSeconds = 3f;
    private float _unconfiguredTimer;
    private bool _unconfiguredReported;

    private void Update()
    {
        if (_backend is null)
        {
            _unconfiguredTimer += Time.unscaledDeltaTime;
            if (!_unconfiguredReported &&
                _unconfiguredTimer >= UnconfiguredErrorAfterSeconds &&
                FindAnyObjectByType<HexLive.UnityPresentation.UI.LoadingScreen>(FindObjectsInactive.Include) == null)
            {
                _unconfiguredReported = true;
                Debug.LogError(
                    "Simulation runner is still UNCONFIGURED after " +
                    $"{UnconfiguredErrorAfterSeconds:0}s: no world JSON asset, nobody called Configure, " +
                    "and no LoadingScreen exists to do it — the world will never be created.", this);
            }

            return;
        }

        _backend.Tick(Time.unscaledDeltaTime);

        // Not while paused — matching the old loop, which early-returned before
        // either of these. It matters most during Continue's offline wind: the
        // loading screen steps the engine with the clock PAUSED, and flushing
        // there would pour the entire multi-day chronicle into the console and
        // the history file mid-load (the old behavior delivered only the ring's
        // last 2048 after unpause). It also kept autosave from rewriting an
        // identical world every minute while the game sits on pause.
        // StepSingleTick flushes explicitly, so debug stepping still logs.
        if (_backend.IsPaused)
        {
            return;
        }

        FlushEvents();
        AutosaveTick();
    }

    // Spec 41.2 v2: autosave — every 60 real seconds while enabled (the
    // loading screen turns this on once the world is presented), plus on quit
    // and on leaving play mode (OnDestroy). The save is the FULL model blob
    // (tens of KB) — still trivial next to a 60-second cadence.
    private const float AutosaveIntervalSeconds = 60f;
    private float _autosaveTimer;

    public bool AutosaveEnabled { get; set; }

    // Dev/test scenes set this: their throwaway worlds must NEVER touch the
    // real hexlive_save.dat — not from the 60s tick, not on quit/play-mode
    // exit, and not via the loader watchdog below (which otherwise flips
    // AutosaveEnabled on in any scene that starts paused without a loading
    // screen — exactly what a test bootstrap does).
    public bool AutosaveSuppressed { get; set; }

    // Spec 41.1 safety net: if the loading coroutine dies without unpausing
    // (an in-play domain reload kills coroutines silently), the game must not
    // stay frozen behind a dead curtain — resume once the loader is gone.
    private float _loaderWatchdog;

    private void LateUpdate()
    {
        if (_backend is null || !_backend.IsPaused || AutosaveEnabled || AutosaveSuppressed)
        {
            return;
        }

        _loaderWatchdog += Time.unscaledDeltaTime;
        if (_loaderWatchdog < 2f)
        {
            return;
        }

        _loaderWatchdog = 0f;
        if (FindAnyObjectByType<UI.LoadingScreen>() == null)
        {
            Debug.LogWarning("[HexLive] Loading screen died without finishing — resuming.");
            _backend.Resume();
            AutosaveEnabled = !AutosaveSuppressed;
        }
    }

    private void AutosaveTick()
    {
        if (!AutosaveEnabled || AutosaveSuppressed || _backend is not { SupportsClientSave: true })
        {
            return;
        }

        _autosaveTimer += Time.unscaledDeltaTime;
        if (_autosaveTimer < AutosaveIntervalSeconds)
        {
            return;
        }

        _autosaveTimer = 0f;
        WriteSaveNow();
    }

    public void WriteSaveNow()
    {
        // Nothing to save when someone else owns the world.
        if (_backend is { SupportsClientSave: true })
        {
            _backend.WriteSaveNow();
        }
    }

    private void OnApplicationQuit()
    {
        if (AutosaveEnabled && !AutosaveSuppressed)
        {
            WriteSaveNow();
        }

        _gameHistory.Flush();
    }

    private void OnDestroy()
    {
        // Editor play-mode exit skips OnApplicationQuit — save here too.
        if (AutosaveEnabled && !AutosaveSuppressed)
        {
            WriteSaveNow();
        }

        _backend?.Shutdown();
        SetPresentationPaused(false);
        if (ReferenceEquals(SimulationSource.Current, this))
        {
            SimulationSource.Current = null;
        }

        _gameHistory.Dispose();
    }

    /// <summary>
    /// Pulls new events off the backend and fans them out to the three sinks
    /// that consume them: the colony history file, the sound layer (§67 voices
    /// discrete world moments straight off the trace stream) and the console.
    /// </summary>
    private void FlushEvents()
    {
        if (_backend is null)
        {
            return;
        }

        _drainedEvents.Clear();
        _lastLoggedSeq = _backend.DrainEvents(_lastLoggedSeq, _drainedEvents);

        var logAllTrace = _logTraceEventsToConsole;
        var logImportant = _logImportantEventsToConsole;

        for (var i = 0; i < _drainedEvents.Count; i++)
        {
            var e = _drainedEvents[i];
            var isGameHistoryEvent = GameHistoryLog.IsGameHistoryEvent(e);
            if (isGameHistoryEvent)
            {
                _gameHistory.Record(e);
            }

            Audio.SoundManager.Instance?.OnSimEvent(e);

            // §121: отказ на приказ игрока — короткая подпись в панели
            // персонажа. В историю колонии он не идёт нарочно: это ответ на
            // клик, а не событие в жизни острова.
            if (e.Type == "ManualOrderRejected" && e.EntityId is { } rejectedNpc)
            {
                Input.ManualOrderFeedback.Report(rejectedNpc, e.Message);
            }
            else if (e.Type == "GroupOrderResult")
            {
                Input.GroupOrderFeedback.Report(e.Message);
            }
            // §121.7: возврат под ИИ по таймауту обязан быть виден — молчаливое
            // «она вдруг зажила своей жизнью» читается как поломка.
            else if (e.Type == "ManualControlExpired" && e.EntityId is { } expiredNpc)
            {
                Input.ManualOrderFeedback.ReportTerm(expiredNpc, "toast.manual_expired");
            }
            // §121.5: приказ был ПРИНЯТ и убит позже (бой, провал пути,
            // исчезнувшая цель) — раньше это гасло в debug-трассе, и игрок
            // читал «стоит и не идёт» как поломку.
            else if (e.Type == "ManualOrderInterrupted" && e.EntityId is { } interruptedNpc)
            {
                Input.ManualOrderFeedback.ReportInterrupted(interruptedNpc, e.Message);
            }

            if (!logAllTrace && !(logImportant && isGameHistoryEvent))
            {
                continue;
            }

            var entityTag = e.EntityId.HasValue ? $"NPC#{e.EntityId.Value}" : "SYS";
            Debug.Log($"[HexLive T{e.Tick}] [{entityTag}] {e.Type}: {e.Message}");
        }

        _gameHistory.Tick();
    }

    /// <summary>
    /// §83.4: on a hosted world the clock is not ours. Pausing is refused HERE
    /// rather than only on the speed bar, because the loudest caller is not a
    /// button at all — <c>GameMenu</c> pauses the simulation before it draws
    /// itself, so opening the menu on a server stopped the colony for every
    /// other viewer, with nothing on screen saying so. The menu's own
    /// <c>Time.timeScale</c> still freezes THIS client's view, which is all it
    /// ever wanted.
    /// </summary>
    public bool CanControlClock => !Link.IsRemote;

    public void Pause()
    {
        if (_backend is null || !CanControlClock)
        {
            return;
        }

        _backend.Pause();
        SetPresentationPaused(true);
    }

    public void Resume()
    {
        if (_backend is null || !CanControlClock)
        {
            return;
        }

        _backend.Resume();
        SetPresentationPaused(false);
    }

    public void TogglePause()
    {
        if (_backend is null)
        {
            return;
        }

        if (_backend.IsPaused)
        {
            Resume();
        }
        else
        {
            Pause();
        }
    }

    private void SetPresentationPaused(bool paused)
    {
        if (paused)
        {
            if (!_presentationPaused)
            {
                _timeScaleBeforePause = Time.timeScale > 0f ? Time.timeScale : 1f;
                _presentationPaused = true;
            }

            // §31.13C: the simulation clock is unscaled, but every character
            // Animator and procedural pose consumes scaled time. Freeze that
            // shared presentation clock so the exact current pose is retained.
            Time.timeScale = 0f;
            return;
        }

        if (!_presentationPaused)
        {
            return;
        }

        Time.timeScale = _timeScaleBeforePause > 0f ? _timeScaleBeforePause : 1f;
        _presentationPaused = false;
    }

    public void SetSpeed(float speedMultiplier) => _backend?.SetSpeed(speedMultiplier);

    public void StepSingleTick()
    {
        _backend?.StepSingleTick();
        FlushEvents();
    }

    public void Configure(TextAsset worldJson, bool startPaused = true, float initialSpeed = 1f)
    {
        _bootstrapAsset ??= gameObject.GetComponent<WorldBootstrapAsset>() ?? gameObject.AddComponent<WorldBootstrapAsset>();
        _bootstrapAsset.SetWorldJson(worldJson);
        _startPaused = startPaused;
        _initialSpeed = initialSpeed;
        Bootstrap();
    }

    public void Configure(WorldBootstrapDefinition definition, bool startPaused = true, float initialSpeed = 1f)
    {
        _startPaused = startPaused;
        _initialSpeed = initialSpeed;
        Bootstrap(definition, null);
    }

    /// <summary>
    /// §41.3 (r3): мир, уже построенный ВОРКЕРОМ за шторкой. Worldgen — чистая
    /// симуляция (тот же довод, что у намотки §41.3 и у удалённого
    /// BuildInitialWorld): на большом острове синхронный Create в главном
    /// потоке замораживал редактор на минуты сразу после «Реестр контента
    /// готов». Экран загрузки строит мир на Task.Run и отдаёт его сюда готовым.
    /// </summary>
    public void Configure(
        WorldBootstrapDefinition definition, HexLive.Simulation.Core.WorldState prebuiltWorld,
        bool startPaused = true, float initialSpeed = 1f)
    {
        _startPaused = startPaused;
        _initialSpeed = initialSpeed;
        Bootstrap(definition, prebuiltWorld);
    }

    private void Bootstrap()
    {
        if (_bootstrapAsset?.WorldJson is null)
        {
            // Expected at Awake in the shipped flow: PrototypeRuntimeBootstrap
            // adds the runner FIRST (Awake fires inside AddComponent) and only
            // then creates the LoadingScreen, which calls Configure(definition)
            // after the menu. A dead world (nothing ever configures us) is
            // detected by the watchdog in Update instead.
            return;
        }

        var definition = WorldBootstrapJsonParser.Parse(_bootstrapAsset.WorldJson.text);
        Bootstrap(definition, null);
    }

    private void Bootstrap(
        WorldBootstrapDefinition definition, HexLive.Simulation.Core.WorldState prebuiltWorld)
    {
        // A remote client does not own a local fallback world. Building one
        // here was pure waste: Continue synchronously generated seed 0,
        // prewarmed its content and only then threw that engine away before
        // opening the socket. The visible result was a several-second frozen
        // curtain before the first "Connecting" frame. The authoritative seed
        // arrives in Handshake; RemoteSocketBackend regenerates exactly that
        // topology on a worker.
        if (TryBootstrapRemote())
        {
            return;
        }

        // §41.3 (r3): экран загрузки строит большой остров на воркере и отдаёт
        // готовый мир; синхронный Create здесь остаётся для dev-сцен и
        // редких fallback-путей с маленькими мирами.
        var world = prebuiltWorld ?? new WorldStateFactory().Create(definition);
        var saveHeader = SaveGame.TryReadHeader();
        _gameHistory.OpenForWorld(world.Seed, saveHeader != null && saveHeader.seed == world.Seed);

        var settings = new SimulationSettings
        {
            TickDeltaTime = definition.Simulation.TickDeltaTime,
            MediumInterval = definition.Simulation.MediumTickInterval,
            SlowInterval = definition.Simulation.SlowTickInterval,
            MaxTicksPerFrame = 64
        };

        var clock = new SimulationClock();
        clock.SetSpeed(_initialSpeed);
        if (_startPaused)
        {
            clock.Pause();
        }
        else
        {
            clock.Resume();
        }

        // §30.17: диагностическая трасса МОЛЧИТ ПО УМОЛЧАНИЮ везде — и в
        // билде, и в редакторе: она стоит ~21 МБ аллокаций на тик на большой
        // карте, а читают её только тогда, когда что-то разбирают. Включают
        // осознанно: ключом -hexlive-trace при запуске или тумблером
        // «Trace» в дебаг-панели прямо во время игры.
        HexLive.Simulation.Runtime.SimTrace.Enabled = TraceRequestedOnCommandLine();

        // Контент, ЗАВИСЯЩИЙ от мира (одежда, причёски, протезы), заказывается
        // здесь всегда: обход Entities.* возможен только пока мир на главном
        // потоке, а заявки не блокируют — они лишь уходят в ContentAssetService. Так
        // гардероб едет параллельно намотке офлайна, а не после неё.
        Wearing.ScenePrewarm.ForWorld(world);

        // Блокирующая часть (Resources, FMOD, префабы мобов) может подождать:
        // экран загрузки позовёт её сам, уже на фоне работающего воркера.
        _contentWarmed = false;
        if (!DeferContentPrewarm)
        {
            WarmContent();
        }

        // Loopback runs the local world through the wire codec, which interns
        // definition ids — build the table here so that path works too.
        HexLive.Simulation.Wire.DefinitionIdTable.Build(world.Content);

        var engine = new SimulationEngine(world, settings, clock);
        // The list itself lives in the simulation assembly so the game, the server
        // and headless probes cannot drift apart — see SimulationSystemRegistry.
        SimulationSystemRegistry.RegisterDefaults(engine);

        // §30.14: бортовой самописец — по кольцу последних событий на каждого
        // NPC, чтобы дебаг-панель могла показать, ЧТО эта делала до того, как
        // застряла. Общее кольцо на 2048 записей на такой вопрос не отвечает:
        // при ~200 событиях в тик оно живёт около одиннадцати тиков. Только в
        // редакторе и development-сборках — там же, где включён полный трейс.
        if (Application.isEditor || UnityEngine.Debug.isDebugBuild)
        {
            engine.World.FlightRecorder = HexLive.Simulation.Runtime.FlightRecorder.ForBehavior();
        }

        _backend?.Shutdown();
        _backend = CreateBackend(new LocalEngineBackend(engine, clock, settings));
        SimulationSource.Current = this;
        _lastLoggedSeq = 0;
    }

    private bool TryBootstrapRemote()
    {
        if (SessionConfig.Mode != SimulationMode.Remote)
        {
            return false;
        }

        var url = SessionConfig.ServerUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            // Defensive fallback for dev callers which selected Remote without
            // an address. LoadingScreen never does this; preserving a local
            // world here keeps those callers usable and retains the old error.
            Debug.LogError("[HexLive] Remote session requested with no server URL — staying local.");
            return false;
        }

        _backend?.Shutdown();
        _contentWarmed = false;
        var remote = new Remote.RemoteSocketBackend(
            url, SessionConfig.ControlToken, SessionConfig.ClientId);
        _backend = remote;
        SimulationSource.Current = this;
        _lastLoggedSeq = 0;
        remote.Connect();
        return true;
    }

    /// <summary>
    /// §41.3: догреть контент мира ПОСЛЕ намотки офлайна.
    ///
    /// Список, собранный до намотки, к её концу устарел: за игровые сутки
    /// колонистка успевает переодеться, умереть в другом, лишиться руки и
    /// получить протез, а в колонию — прийти новая девушка со своей причёской.
    /// Прогрев по старому списку оставил бы всё это на ленивом пути, то есть на
    /// блокирующем чтении в первом же кадре игры.
    ///
    /// Что именно греть — знает <see cref="Wearing.ScenePrewarm"/>, и это
    /// единственное место, куда добавляется новая семья контента. Повторный
    /// проход дешёвый: каждая дверь молчит на уже приехавшем.
    /// </summary>
    public void WarmForWorld()
    {
        if (Engine is { } engine)
        {
            Wearing.ScenePrewarm.ForWorld(engine.World);
        }
    }

    /// <summary>
    /// Прогрев арта: всё, что иначе прочиталось бы с диска в первый раз посреди
    /// игры, внутри тика, на главном потоке. Вызывается либо из
    /// <c>Bootstrap</c> (обычный путь), либо экраном загрузки параллельно
    /// намотке офлайна (<see cref="DeferContentPrewarm"/>).
    ///
    /// Главный поток блокируется — это чтения с диска, и они блокировали его и
    /// раньше. Смысл переноса в том, ЧТО происходит в это время: не «намотка
    /// ждёт», а «намотка идёт на воркере». Идемпотентен: второй вызов молчит.
    ///
    /// ⚠️ Ничего из этого не читает состояние мира — только каталоги и диск.
    /// Список одежды собран заранее, в <c>Bootstrap</c>. Так и должно остаться:
    /// в момент вызова мир может принадлежать другому потоку.
    /// </summary>
    public void WarmContent()
    {
        if (_contentWarmed)
        {
            return;
        }

        _contentWarmed = true;

        // Spec 40.8-G: pull all wound/blood art into memory NOW, behind the
        // loading curtain — lazily loading it on the first landed bite cost a
        // ~2.4 s File.Read burst mid-combat (frame #20 of the deep capture).
        Wearing.SkinTexturePainter.Prewarm();
        Wearing.GarmentWearPainter.Prewarm();
        Rendering.MobWoundPainter.Prewarm();
        _ = Rendering.BloodSplashVfx.Prefabs;
        HexLive.UnityPresentation.Content.AtomicResources.Prewarm("HexLive/NpcAnimSet");
        HexLive.UnityPresentation.Content.AtomicResources.Prewarm("HexLive/CarryPoses/Carrying");
        HexLive.UnityPresentation.Content.AtomicResources.Prewarm("HexLive/CarryPoses/BeingCarried");
        // Spec §67: every SFX sample loads NOW for the same reason — the FMOD
        // createSound file reads must not land on the first mid-game chop.
        Audio.FmodSfx.Prewarm();
        // Mob prefabs too (wolf FBX + pelt textures): the view spawns the
        // moment the sim spawns the mob — mid-game, typically right before
        // the first fight — and the lazy Resources.Load there was the
        // "small freeze just before the first combat".
        foreach (var mobId in Config.MobLibrary.Ids)
        {
            Config.MobLibrary.LoadPrefab(mobId);
        }

        // §152: this starts the live-registry delta. It does NOT warm icons:
        // every icon is inside its owning object's bundle, so warming all icons
        // would also download every garment, prop and building.
        Wearing.Garments.ItemIcons.PrewarmAll();
    }

    /// <summary>
    /// Picks the backend for a world which was deliberately built locally.
    /// Remote mode returns earlier through <see cref="TryBootstrapRemote"/> so
    /// pressing Continue never creates and discards a seed-0 fallback world.
    /// </summary>
    private static ISimulationBackend CreateBackend(LocalEngineBackend local)
    {
        switch (SessionConfig.Mode)
        {
            case SimulationMode.Loopback:
                return new LoopbackBackend(local);

            default:
                return local;
        }
    }
}

}
