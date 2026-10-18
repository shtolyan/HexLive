#if UNITY_EDITOR
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using HexLive.UnityPresentation.Bootstrap;
using HexLive.UnityPresentation.Input;
using HexLive.UnityPresentation.Platform;
using HexLive.UnityPresentation.Rendering;
using HexLive.UnityPresentation.UI;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace HexLive.Tests
{
    public sealed class HtmlCharacterFollowRuntimeTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private static object Get(object owner, string name) => owner.GetType().GetField(name, Private).GetValue(owner);
        private static void Set(object owner, string name, object value) => owner.GetType().GetField(name, Private).SetValue(owner, value);
        private static void Call(object owner, string name, params object[] args) => owner.GetType().GetMethod(name, Private).Invoke(owner, args);
        private static readonly FieldInfo Bits = typeof(HtmlUi).GetField("_bits", BindingFlags.Static | BindingFlags.NonPublic);

        [UnityTest]
        public IEnumerator HtmlSelectionAndRepeatFollowWorkWithoutLettingMenuInputIntoWorld()
        {
            SimDataFile.Require(Path.Combine(Application.dataPath, "..", "SimData", "simdata.json"));
            var definition = PrototypeWorldDefinitionFactory.Create(271816231);
            var world = new WorldStateFactory().Create(definition);
            var options = new SimulationSettings { TickDeltaTime = definition.Simulation.TickDeltaTime,
                MediumInterval = definition.Simulation.MediumTickInterval, SlowInterval = definition.Simulation.SlowTickInterval };
            var clock = new SimulationClock();
            var engine = new SimulationEngine(world, options, clock);
            var people = world.Entities.Npcs.Values.Where(n => n.Faction == Faction.Colony).Take(2).ToArray();
            Assert.That(people.Length, Is.EqualTo(2));
            foreach (var person in people) world.PlayerControlledNpcs.Add(person.Id.Value);
            var owner = new GameObject("Isolated HTML camera test");
            owner.SetActive(false); // Never start a world, import content or connect to a server.
            var runner = owner.AddComponent<SimulationRunnerBehaviour>();
            Set(runner, "_backend", new LocalEngineBackend(engine, clock, options));
            owner.AddComponent<Camera>();
            var camera = owner.AddComponent<RtsCameraController>();
            camera.SetRunner(runner);
            Set(camera, "_worldRenderer", owner.AddComponent<HexWorldRenderer>());
            var panel = owner.AddComponent<CharacterPanel>();
            panel.SetRunner(runner);
            var selection = NpcSelection.SelectedIds.ToArray();
            var oldBits = Bits.GetValue(null);
            var background = InputSystem.settings.backgroundBehavior;
            var updateMode = InputSystem.settings.updateMode;
            var keyboard = InputSystem.AddDevice<Keyboard>();
            var mouse = InputSystem.AddDevice<Mouse>();
            try
            {
                InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
                InputSystem.settings.updateMode = InputSettings.UpdateMode.ProcessEventsManually;
                NpcSelection.Clear();
                Call(camera, "Start");
                Call(camera, "OnEnable");
                Bits.SetValue(null, 1); // Cursor stays over an HTML roster card.
                Call(camera, "LateUpdateCore"); // Settle the initial rig before measuring clicks.
                InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.W));
                InputSystem.QueueStateEvent(mouse, new MouseState { scroll = new Vector2(0, 1) }.WithButton(MouseButton.Left));
                InputSystem.Update();
                var initialPosition = camera.transform.position;
                var initialPivot = (Vector3)Get(camera, "_freePivot");
                var initialDistance = (float)Get(camera, "_requestedDistance");
                Call(panel, "OnHtmlCommand", "select", new JObject { ["id"] = people[0].Id.Value });
                Call(camera, "LateUpdateCore");
                Assert.That(Get(camera, "_mode").ToString(), Is.EqualTo("Free"));
                Assert.That((Vector3)Get(camera, "_freePivot"), Is.EqualTo(initialPivot), "W over HTML must not pan.");
                Assert.That((float)Get(camera, "_requestedDistance"), Is.EqualTo(initialDistance), "Scroll over HTML must not zoom.");
                Assert.That(Get(camera, "_pendingFrameSelection"), Is.False, "First click only selects.");
                Call(panel, "OnHtmlCommand", "select", new JObject { ["id"] = people[0].Id.Value });
                for (var i = 0; i < 12; i++)
                {
                    yield return null;
                    Call(camera, "LateUpdateCore");
                }
                Assert.That(Get(camera, "_mode").ToString(), Is.EqualTo("Orbit"), "Blocked W must not detach follow.");
                Assert.That((bool)Get(camera, "_pendingFrameSelection"), Is.False, "Follow must run without moving the pointer off HTML.");
                Assert.That(Vector3.Distance(camera.transform.position, initialPosition), Is.GreaterThan(.01f));
                Assert.That(NpcSelection.PrimaryId, Is.EqualTo(people[0].Id.Value), "The held click must not select the world under the card.");

                var previousTarget = (Vector3)Get(camera, "_smoothedTarget");
                people[0].Position += new Float2(3f, 0f);
                engine.Step(); // New snapshot tick; no autonomous systems or world actions are registered.
                Call(camera, "LateUpdateCore");
                Assert.That(Vector3.Distance((Vector3)Get(camera, "_smoothedTarget"), previousTarget), Is.GreaterThan(2f),
                    "Follow must track a moving NPC while the pointer remains over HTML.");

                var followedPivot = (Vector3)Get(camera, "_smoothedPivot");
                Call(panel, "OnHtmlCommand", "select", new JObject { ["id"] = people[1].Id.Value });
                Assert.That(Get(camera, "_mode").ToString(), Is.EqualTo("Free"), "Selecting a different character detaches follow.");
                Assert.That((Vector3)Get(camera, "_smoothedPivot"), Is.EqualTo(followedPivot), "Switching selection must not pull toward the new target.");
                Call(panel, "OnHtmlCommand", "select", new JObject { ["id"] = people[1].Id.Value });
                Bits.SetValue(null, 2); // A press dragged off HTML is still owned by the menu.
                Call(camera, "LateUpdateCore");
                Assert.That(Get(camera, "_mode").ToString(), Is.EqualTo("Orbit"));
                Assert.That((bool)Get(camera, "_pendingFrameSelection"), Is.False);

                InputSystem.QueueStateEvent(mouse, new MouseState());
                InputSystem.Update();
                Bits.SetValue(null, 0);
                Call(camera, "LateUpdateCore");
                Assert.That(Get(camera, "_mode").ToString(), Is.EqualTo("Free"), "W outside HTML must still detach follow.");
            }
            finally
            {
                Call(camera, "OnDisable");
                Bits.SetValue(null, oldBits);
                InputSystem.settings.backgroundBehavior = background;
                InputSystem.settings.updateMode = updateMode;
                InputSystem.RemoveDevice(mouse);
                InputSystem.RemoveDevice(keyboard);
                NpcSelection.ReplaceMany(selection, requestFrame: false);
                Object.DestroyImmediate(owner);
            }
        }
    }
}
#endif
