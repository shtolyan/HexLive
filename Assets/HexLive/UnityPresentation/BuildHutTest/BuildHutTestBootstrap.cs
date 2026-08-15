using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Runtime;
using UnityEngine;

namespace HexLive.UnityPresentation.BuildHutTest
{
    /// <summary>
    /// The whole of the BuildHutTest scene.
    ///
    /// It is deliberately the INVERSE of every other dev scene here. The others
    /// create their own <c>SimulationRunnerBehaviour</c> in Awake, and that is
    /// precisely the flag <c>PrototypeRuntimeBootstrap.Boot</c> checks before it
    /// bails — which is why AmputationTest, SwimTest, BedBuildTest and the rest
    /// have no menus, no loading screen, no RTS camera and no HUD. This one
    /// builds nothing. It only says which world to make, so the ordinary game
    /// assembles itself on top: the same runner, renderer, sky, sound, music,
    /// camera, character panel, inspector, context menu, speed bar, history,
    /// game menu — everything. The map is the only difference.
    ///
    /// Drop it on one empty GameObject in a copy of Main.unity and nothing else.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BuildHutTestBootstrap : MonoBehaviour
    {
        [Tooltip("Мир пересобирается на этом сиде. Меняй, чтобы прогнать другую попытку стройки.")]
        [SerializeField] private int _seed = 12345;

        [Tooltip("Мясо в песочнице не портится, чтобы стройку не прерывал голод.")]
        [SerializeField] private bool _disableMeatSpoilage = true;

        private int _restoreRawSpoilTicks;
        private int _restoreCookedSpoilTicks;
        private bool _restoreSpoilage;

        private void Awake()
        {
            // Awake beats the [RuntimeInitializeOnLoadMethod(AfterSceneLoad)]
            // hook, and both run long before LoadingScreen asks for a world.
            PrototypeWorldDefinitionFactory.Override = BuildHutTestWorld.Create;

            // An autosave from a sandbox would overwrite the player's real save.
            // LoadingScreen turns autosaving on when it finishes, so the runner
            // is muzzled in Start, after it exists — see below.
        }

        private void Start()
        {
            SuppressAutosave();
            if (!_disableMeatSpoilage) return;

            // Tuning is loaded by PrototypeRuntimeBootstrap.Boot BEFORE the world
            // exists, so anything set in Awake would be overwritten. Start is
            // after that. Loose meat placed through ObjectBootstrap already never
            // spoils (AddObject leaves SpawnTick at 0 and MeatSpoilageSystem skips
            // those), so this only covers meat the colony cooks during the run.
            _restoreRawSpoilTicks = SimBalance.MeatRawSpoilTicks;
            _restoreCookedSpoilTicks = SimBalance.MeatCookedSpoilTicks;
            _restoreSpoilage = true;
            SimBalance.MeatRawSpoilTicks = int.MaxValue;
            SimBalance.MeatCookedSpoilTicks = int.MaxValue;
        }

        /// <summary>
        /// The sandbox must never touch <c>hexlive_save.dat</c>. The runner is
        /// created by the ordinary bootstrap, so it may not exist on the first
        /// frame; keep asking until it does.
        /// </summary>
        private void SuppressAutosave()
        {
            var runner = FindAnyObjectByType<HexLive.UnityPresentation.Bootstrap.SimulationRunnerBehaviour>();
            if (runner == null)
            {
                Invoke(nameof(SuppressAutosave), 0.25f);
                return;
            }
            runner.AutosaveSuppressed = true;
            runner.AutosaveEnabled = false;
        }

        private void OnDestroy()
        {
            // Static state outlives the scene: a stale override would follow the
            // player into an ordinary new game and hand them this sandbox island.
            if (PrototypeWorldDefinitionFactory.Override == BuildHutTestWorld.Create)
                PrototypeWorldDefinitionFactory.Override = null;
            if (!_restoreSpoilage) return;
            SimBalance.MeatRawSpoilTicks = _restoreRawSpoilTicks;
            SimBalance.MeatCookedSpoilTicks = _restoreCookedSpoilTicks;
            _restoreSpoilage = false;
        }
    }
}
