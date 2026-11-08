using System;
using System.Linq;
using HexLive.Simulation.Debug;

namespace HexLive.UnityPresentation.UI
{
    // §114.8: copy the viewer's existing snapshot, never consult Engine.World.
    [Serializable]
    public sealed class BugReportContextData
    {
        public int schemaVersion = 1, seed, tick;
        public bool truncated;
        public string utc, version, buildGuid, platform, language, scene, worldId;
        public int screenWidth, screenHeight;
        public float cameraX, cameraY, cameraZ, cameraPitch, cameraYaw, cameraZoom;
        public string[] windows = Array.Empty<string>();
        public Npc selected;
        public Npc[] nearbyNpcs = Array.Empty<Npc>();
        public Mob[] nearbyMobs = Array.Empty<Mob>();
        public Object[] nearbyObjects = Array.Empty<Object>();
        public Event[] events = Array.Empty<Event>();
        [Serializable] public sealed class Npc
        {
            public int id, faction, tileQ, tileR, perceptionRadius, knownObjectCount;
            public float x, y, health, hunger, thirst, energy, social, comfort, stress;
            public bool manual;
            public string body, skin, hair, hairColour, goal, plan, movement, execution, interaction, heldItem;
            public int targetObjectId;
            public string[] inventory, worn, knownObjects, cooldowns, scores;
            public int[] path;
        }
        [Serializable] public sealed class Object
        {
            public int id, tileQ, tileR;
            public float tileCenterX, tileCenterY, durability;
            public string definition, buildProduct;
            public int[] delivered, required;
            public int[] junctions;
        }
        [Serializable] public sealed class Event
        { public int tick, entityId; public string type, detail; }
        [Serializable] public sealed class Mob
        { public int id, tileQ, tileR, targetNpcId; public float x, y, health; public string kind, status; public bool attacking; }

        public static BugReportContextData Capture(WorldSnapshot snapshot, int selectedId)
        {
            var result = new BugReportContextData { utc = DateTime.UtcNow.ToString("O") };
            if (snapshot == null) return result;
            result.seed = snapshot.Seed; result.tick = snapshot.Tick;
            var selected = snapshot.Npcs.FirstOrDefault(n => n.Id.Value == selectedId);
            if (selected != null) result.selected = Copy(selected, true);
            // Proximity is useful context, not a claim that the NPC perceived them.
            var center = selected?.Position ?? (snapshot.Npcs.Count > 0 ? snapshot.Npcs[0].Position : default);
            result.nearbyNpcs = snapshot.Npcs.Where(n => n != selected)
                .OrderBy(n => Distance2(n.Position, center)).ThenBy(n => n.Id.Value).Take(12)
                .Select(n => Copy(n, false)).ToArray();
            result.nearbyMobs = snapshot.Mobs.OrderBy(m => Distance2(m.Position, center)).ThenBy(m => m.Id).Take(12)
                .Select(m => new Mob { id = m.Id, kind = Bound(m.MobId), status = Bound(m.Status),
                    tileQ = m.Tile.Q, tileR = m.Tile.R, x = m.Position.X, y = m.Position.Y,
                    health = m.Health, targetNpcId = m.TargetNpcId, attacking = m.IsAttacking }).ToArray();
            result.nearbyObjects = snapshot.Objects.OrderBy(o => Distance2(HexLive.Simulation.Spatial.HexSpatialMath.TileToWorld(o.Tile), center))
                .ThenBy(o => o.Id.Value).Take(24).Select(o => new Object
                {
                    id = o.Id.Value, definition = Bound(o.DefinitionId), tileQ = o.Tile.Q, tileR = o.Tile.R,
                    tileCenterX = HexLive.Simulation.Spatial.HexSpatialMath.TileToWorld(o.Tile).X,
                    tileCenterY = HexLive.Simulation.Spatial.HexSpatialMath.TileToWorld(o.Tile).Y,
                    durability = o.Durability, buildProduct = Bound(o.BuildProduct), junctions = o.Junctions.Take(32).Select(j => j.Value).ToArray(),
                    delivered = new[] { o.DeliveredLogs, o.DeliveredStones, o.DeliveredLeaves, o.DeliveredSticks, o.DeliveredRope, o.DeliveredBoards },
                    required = new[] { o.BillLogs, o.BillStones, o.BillLeaves, o.BillSticks, o.BillRope, o.BillBoards }
                }).ToArray();
            var allowed = new[] { "PlanFailed", "PathFailed", "CraftBlocked", "GoalSelected", "ManualOrderRejected", "WorldOrderRejected" };
            result.events = snapshot.TraceEvents.Where(e => (e.EntityId == selectedId || e.EntityId == null) && allowed.Contains(e.Type))
                .Reverse().Take(16).Select(e => new Event { tick = e.Tick, entityId = e.EntityId ?? -1,
                    type = e.Type, detail = SafeDetail(e.Message) }).ToArray();
            return result;
        }
        private static Npc Copy(NpcSnapshot n, bool details) => new()
        {
            id = n.Id.Value, faction = (int)n.Faction, tileQ = n.Tile.Q, tileR = n.Tile.R,
            x = n.Position.X, y = n.Position.Y, health = n.Health, hunger = n.Hunger, thirst = n.Thirst,
            energy = n.Energy, social = n.Social, comfort = n.Comfort, stress = n.Stress,
            manual = n.IsManualControl, body = Bound(n.ActorMesh), skin = Bound(n.SkinSet),
            hair = Bound(n.Hairstyle), hairColour = Bound(n.HairColour), goal = Bound(n.CurrentGoal),
            plan = Bound(n.PlanStatus), movement = Bound(n.MovementStatus), execution = Bound(n.ExecutionStatus),
            interaction = Bound(n.CurrentInteraction), heldItem = Bound(n.HeldItemId), targetObjectId = n.TargetObjectId ?? -1,
            perceptionRadius = n.PerceptionRadiusTiles, knownObjectCount = n.KnownObjectCount,
            inventory = details ? n.InventoryItems.Take(64).Select(Bound).ToArray() : Array.Empty<string>(),
            worn = details ? n.WornItems.Take(32).Select(Bound).ToArray() : Array.Empty<string>(),
            knownObjects = details ? n.KnownObjects.Take(48).Select(Bound).ToArray() : Array.Empty<string>(),
            cooldowns = details ? n.CooldownGoals.Take(16).Select(Bound).ToArray() : Array.Empty<string>(),
            scores = details ? n.GoalScores.OrderByDescending(g => g.FinalScore).Take(12)
                .Select(g => Bound(g.Goal) + "=" + g.FinalScore.ToString("G4", System.Globalization.CultureInfo.InvariantCulture)).ToArray() : Array.Empty<string>(),
            path = details ? n.Path.Take(32).Select(j => j.Value).ToArray() : Array.Empty<int>()
        };
        private static string Bound(string text) => text == null ? "" : text.Substring(0, Math.Min(text.Length, 160));
        private static float Distance2(HexLive.Simulation.Common.Float2 a, HexLive.Simulation.Common.Float2 b)
        { var d = a - b; return d.X * d.X + d.Y * d.Y; }
        private static string SafeDetail(string text)
        {
            text = Bound(text);
            foreach (var term in new[] { "token", "bearer", "password", "account", "playerId", ":\\", "/Users/", "/home/" })
                if (text.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0) return "";
            return text;
        }
    }
}
