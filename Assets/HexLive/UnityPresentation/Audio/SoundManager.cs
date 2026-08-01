#nullable enable
using System.Collections.Generic;
using HexLive.Simulation.Runtime;
using UnityEngine;

namespace HexLive.UnityPresentation.Audio
{
    /// <summary>
    /// Spec §67: the colony's ears. Two feeds drive all audio:
    ///   1) discrete sim trace events (tree felled, bite landed, death…) —
    ///      pushed here from SimulationRunnerBehaviour.FlushEvents;
    ///   2) the island ambience state machine (shore waves, day birds, night
    ///      crickets, geckos, rain) — polled per frame from the snapshot.
    /// Continuous per-view sounds (footsteps, chop swings, whooshes) live in
    /// the views themselves (NpcActorView) and call FmodSfx directly.
    /// Everything is 3D-positioned via the renderer's view dictionaries; an
    /// event whose position can't be resolved stays silent rather than playing
    /// at full volume in the listener's face.
    /// </summary>
    public sealed class SoundManager : MonoBehaviour
    {
        public static SoundManager? Instance { get; private set; }

        // Fast-forward guard (spec §67.2): above this sim speed the discrete
        // one-shots go quiet — a 50× colony must not scream. Ambience stays.
        private const float MaxAudibleSimSpeed = 4.01f;
        // Events older than this many ticks are history (save replay, buffer
        // catch-up after a stall) — never voiced.
        private const int StaleEventTicks = 12;

        private Bootstrap.SimulationRunnerBehaviour? _runner;
        private Rendering.HexWorldRenderer? _renderer;

        private readonly Dictionary<string, float> _lastPlayedAt = new();
        private readonly List<(float at, string id, Vector3 pos, float gain)> _delayed = new();

        // ---- ambience state ----
        private FmodSfx.Loop _waves;
        private FmodSfx.Loop _jungle;
        private FmodSfx.Loop _crickets;
        private FmodSfx.Loop _rain;
        private float _jungleVol;
        private float _cricketsVol;
        private float _rainVol;
        private float _nextGeckoAt;
        private float _nextHowlAt;

        private void Awake()
        {
            Instance = this;
        }

        private void OnDestroy()
        {
            FmodSfx.StopLoop(ref _waves);
            FmodSfx.StopLoop(ref _jungle);
            FmodSfx.StopLoop(ref _crickets);
            FmodSfx.StopLoop(ref _rain);
            if (Instance == this)
            {
                Instance = null;
            }
        }

        public void Construct(
            Bootstrap.SimulationRunnerBehaviour runner,
            Rendering.HexWorldRenderer renderer)
        {
            _runner = runner;
            _renderer = renderer;
        }

        // This component is wired ONLY by Construct — there was no fallback, so
        // any change to bootstrap ordering killed every sim-driven sound with no
        // error at all. Fall back to the ambient source rather than going quiet.
        // The `_runner != null` test is deliberately on the CONCRETE type so
        // Unity's destroyed-object equality applies; an interface-typed null
        // check would call a destroyed runner alive.
        private Bootstrap.ISimulationSource? Source =>
            _runner != null ? (Bootstrap.ISimulationSource)_runner : Bootstrap.SimulationSource.Current;

        // ------------------------------------------------------------------
        // Discrete sim events → positioned one-shots.
        // ------------------------------------------------------------------
        public void OnSimEvent(SimulationEvent e)
        {
            var source = Source;
            if (source == null || !source.IsReady || UI.LoadingScreen.IsReplaying)
            {
                return;
            }

            // Save-replay / catch-up floods arrive as a burst of old ticks.
            // Measured against the source's current tick, which is the last tick
            // presentation considers real — not a raw WorldState.Tick that could
            // be ahead of what the player is actually being shown.
            if (source.CurrentTick - e.Tick > StaleEventTicks)
            {
                return;
            }

            if (source.SpeedMultiplier > MaxAudibleSimSpeed)
            {
                return;
            }

            switch (e.Type)
            {
                case "TreeChopped":
                    if (TryNpcPos(e, out var fell))
                    {
                        Play(FmodSfx.Sfx.TreeCreak, fell);
                        PlayDelayed(FmodSfx.Sfx.ChopAccent, fell, 0.55f);
                    }
                    break;
                case "CrownChopped":
                case "LogSplit":
                    if (TryNpcPos(e, out var split))
                    {
                        Play(FmodSfx.Sfx.ChopAccent, split);
                    }
                    break;
                case "BoulderBroken":
                    if (TryNpcPos(e, out var boulder))
                    {
                        Play(FmodSfx.Sfx.MineStone, boulder, 1.1f);
                    }
                    break;
                case "CoconutProcessed":
                    if (TryNpcPos(e, out var coco))
                    {
                        Play(FmodSfx.Sfx.ChopCoco, coco);
                    }
                    break;
                case "DogAggro":
                    if (TryMobPosFromMessage(e.Message, out var aggro))
                    {
                        Play(FmodSfx.Sfx.WolfGrowl, aggro);
                    }
                    break;
                case "DogFight":
                    // Two shapes share the type (§29C.3): "Dog=N bit: …" is the
                    // wolf's landed bite on the NPC; "Dog=N struck -…" is her
                    // weapon landing on the wolf. Entity is the NPC either way.
                    if (TryNpcPos(e, out var fight))
                    {
                        Play(e.Message.Contains(" bit:")
                            ? FmodSfx.Sfx.WolfBite
                            : FmodSfx.Sfx.HitFlesh, fight);
                    }
                    break;
                case "DogKilled":
                    if (TryMobPosFromMessage(e.Message, out var died))
                    {
                        Play(FmodSfx.Sfx.BodyFall, died);
                    }
                    break;
                case "SharkBite":
                    if (TryNpcPos(e, out var shark))
                    {
                        Play(FmodSfx.Sfx.Splash, shark);
                        Play(FmodSfx.Sfx.HitFlesh, shark);
                    }
                    break;
                case "LimbSevered":
                    if (TryNpcPos(e, out var sever))
                    {
                        Play(FmodSfx.Sfx.HitFlesh, sever, 1.15f);
                    }
                    break;
                case "NpcDied":
                case "Murdered":
                case "BledOut":
                case "StarvedToDeath":
                    // §67.10: предсмертный вскрик — реплика самой умирающей
                    // (свой голос + череп в бабле); нет голоса → общий DeathF.
                    if (!TrySay(e, "hurt_death") && TryNpcPos(e, out var death))
                    {
                        Play(FmodSfx.Sfx.DeathF, death);
                    }
                    break;
                case "Fainted":
                case "Collapsed":
                    if (TryNpcPos(e, out var faint))
                    {
                        Play(FmodSfx.Sfx.BodyFall, faint);
                        TrySay(e, "hurt_faint");
                    }
                    break;
                case "HelpCry":
                    if (TryNpcPos(e, out var cry))
                    {
                        Play(FmodSfx.Sfx.HurtF, cry);
                    }
                    break;
                case "Butchered":
                    if (TryNpcPos(e, out var butcher))
                    {
                        Play(FmodSfx.Sfx.HitFlesh, butcher, 0.7f);
                    }
                    break;
                case "FurnitureBuilt":
                case "HutCompleted":
                    if (TryNpcPos(e, out var built))
                    {
                        Play(FmodSfx.Sfx.Hammer, built, 1.1f);
                        // §67.10: «готово!» — с галочкой в бабле.
                        TrySay(e, "happy_done");
                    }
                    break;
            }
        }

        // §67.10: route a sim event to the utterance of the NPC it happened to.
        // Returns false when there is no view (off-screen actor, replay) so the
        // caller can fall back to a plain positional sfx.
        private bool TrySay(SimulationEvent e, string speechId)
        {
            return e.EntityId.HasValue && _renderer != null &&
                   _renderer.TryGetActorView(e.EntityId.Value, out var view) &&
                   view.Say(speechId);
        }

        private bool TryNpcPos(SimulationEvent e, out Vector3 pos)
        {
            pos = default;
            return e.EntityId.HasValue && _renderer != null &&
                   _renderer.TryGetNpcViewPosition(e.EntityId.Value, out pos);
        }

        // System-emitted wolf events carry no entity id — the mob id rides in
        // the message as "Dog={id} …".
        private bool TryMobPosFromMessage(string message, out Vector3 pos)
        {
            pos = default;
            var start = message.IndexOf("Dog=", System.StringComparison.Ordinal);
            if (start < 0 || _renderer == null)
            {
                return false;
            }

            start += 4;
            var end = start;
            while (end < message.Length && char.IsDigit(message[end]))
            {
                end++;
            }

            return end > start &&
                   int.TryParse(message[start..end], out var mobId) &&
                   _renderer.TryGetMobViewPosition(mobId, out pos);
        }

        // Per-id real-time gate so one busy tick can't stack ten copies.
        private void Play(string id, Vector3 pos, float gain = 1f)
        {
            var minGap = id switch
            {
                FmodSfx.Sfx.WolfGrowl => 2.5f,
                FmodSfx.Sfx.DeathF => 1.0f,
                FmodSfx.Sfx.HurtF => 0.5f,
                _ => 0.09f,
            };
            if (_lastPlayedAt.TryGetValue(id, out var last) && Time.time - last < minGap)
            {
                return;
            }

            _lastPlayedAt[id] = Time.time;
            FmodSfx.Play(id, pos, gain);
        }

        /// <summary>One-shot on a real-time fuse (tree-fall crash after the
        /// creak, splash at the landing beat of a dive).</summary>
        public void PlayDelayed(string id, Vector3 pos, float delaySeconds, float gain = 1f)
        {
            _delayed.Add((Time.time + delaySeconds, id, pos, gain));
        }

        // ------------------------------------------------------------------
        // Frame pump: listener, delayed shots, island ambience.
        // ------------------------------------------------------------------
        private void LateUpdate()
        {
            var cam = Camera.main;
            if (cam != null)
            {
                FmodSfx.UpdateListener(cam.transform);
            }

            for (var i = _delayed.Count - 1; i >= 0; i--)
            {
                if (Time.time >= _delayed[i].at)
                {
                    Play(_delayed[i].id, _delayed[i].pos, _delayed[i].gain);
                    _delayed.RemoveAt(i);
                }
            }

            UpdateAmbience(cam);
        }

        // Spec §67.3: waves live on the stretch of shore nearest the camera —
        // ONE virtual 3D emitter that slides along the waterline, so zooming
        // toward the sea swells the surf naturally. Day birds and night
        // crickets are 2D beds crossfaded on the sim clock (0 = 06:00); rain
        // follows Environment.IsRaining (level-triggered — replay-proof);
        // geckos chirp from random jungle directions at night, a lone wolf
        // howls far away once in a while.
        private void UpdateAmbience(Camera? cam)
        {
            if (_renderer == null || UI.LoadingScreen.IsReplaying || cam == null)
            {
                return;
            }

            var snapshot = Source?.CreateSnapshot();
            if (snapshot == null)
            {
                return;
            }

            var listener = cam.transform.position;

            // -- прибой --
            if (_renderer.TryGetNearestShorePoint(listener, out var shore))
            {
                if (!_waves.IsValid)
                {
                    _waves = FmodSfx.StartLoop(FmodSfx.Sfx.LoopWaves, shore);
                }
                else
                {
                    FmodSfx.MoveLoop(ref _waves, shore);
                }
            }

            // -- день/ночь: птицы ↔ сверчки (плавный час на смену) --
            var t = snapshot.TimeOfDayNormalized; // 0 = 06:00
            var day = t is < 0f or > 0.5f
                ? 0f
                : Mathf.Min(
                    Mathf.Clamp01(t / 0.04f),
                    Mathf.Clamp01((0.5f - t) / 0.04f));

            var raining = snapshot.IsRaining;
            var jungleTarget = day * (raining ? 0.25f : 1f);
            var cricketsTarget = (1f - day) * (raining ? 0.4f : 1f);
            var rainTarget = raining ? 1f : 0f;

            EnsureBed(ref _jungle, FmodSfx.Sfx.LoopJungleDay);
            EnsureBed(ref _crickets, FmodSfx.Sfx.LoopCrickets);
            EnsureBed(ref _rain, FmodSfx.Sfx.LoopRain);

            var k = 1f - Mathf.Exp(-1.2f * Time.deltaTime);
            _jungleVol = Mathf.Lerp(_jungleVol, jungleTarget, k);
            _cricketsVol = Mathf.Lerp(_cricketsVol, cricketsTarget, k);
            _rainVol = Mathf.Lerp(_rainVol, rainTarget, k);
            FmodSfx.SetLoopVolume(ref _jungle, FmodSfx.Sfx.LoopJungleDay, _jungleVol);
            FmodSfx.SetLoopVolume(ref _crickets, FmodSfx.Sfx.LoopCrickets, _cricketsVol);
            FmodSfx.SetLoopVolume(ref _rain, FmodSfx.Sfx.LoopRain, _rainVol);

            // -- ночные солисты: гекконы близко, волчий вой далеко --
            if (day < 0.5f)
            {
                if (_nextGeckoAt <= 0f)
                {
                    _nextGeckoAt = Time.time + Random.Range(8f, 25f);
                }

                if (Time.time >= _nextGeckoAt)
                {
                    _nextGeckoAt = Time.time + Random.Range(20f, 70f);
                    var dir = Random.insideUnitCircle.normalized;
                    var at = listener + new Vector3(dir.x, 0f, dir.y) * Random.Range(8f, 16f);
                    at.y = Mathf.Max(0f, listener.y - 4f);
                    FmodSfx.Play(FmodSfx.Sfx.Gecko, at);
                }

                if (_nextHowlAt <= 0f)
                {
                    _nextHowlAt = Time.time + Random.Range(60f, 180f);
                }

                if (Time.time >= _nextHowlAt)
                {
                    _nextHowlAt = Time.time + Random.Range(120f, 360f);
                    var dir = Random.insideUnitCircle.normalized;
                    var at = listener + new Vector3(dir.x, 0f, dir.y) * Random.Range(28f, 40f);
                    FmodSfx.Play(FmodSfx.Sfx.WolfHowl, at);
                }
            }
            else
            {
                _nextGeckoAt = 0f;
                _nextHowlAt = 0f;
            }
        }

        private static void EnsureBed(ref FmodSfx.Loop loop, string id)
        {
            if (!loop.IsValid)
            {
                loop = FmodSfx.StartLoop(id, Vector3.zero, 0f);
            }
        }
    }
}
