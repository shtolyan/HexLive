using System;
using System.Collections.Generic;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Core;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Spatial;

namespace HexLive.Simulation.Runtime
{
    /// <summary>§123.6: deterministic actor-to-junction group placement.</summary>
    public static class GroupFormationPlanner
    {
        public sealed class Assignment
        {
            public NPCState Npc { get; set; }
            public JunctionId Destination { get; set; }
            public long PathCost { get; set; }
        }

        public sealed class Result
        {
            public List<Assignment> Assignments { get; } = new();
            public int CandidateCount { get; set; }
        }

        public static Result Plan(WorldState world, IReadOnlyList<NPCState> actors, Float2 click)
        {
            var result = new Result();
            if (actors is null || actors.Count == 0) return result;

            var orderedActors = new List<NPCState>(actors);
            orderedActors.Sort((a, b) => a.Id.Value.CompareTo(b.Id.Value));
            var participantIds = new HashSet<EntityId>();
            foreach (var actor in orderedActors) participantIds.Add(actor.Id);

            var allCluster = CollectClusterJunctions(world, click);
            var center = SpatialQueries.FindNearestJunction(world, click);
            if (center is null || allCluster.Count == 0) return result;
            var graphDistance = ClusterDistances(world, center.Value, allCluster);
            var candidates = FilterCandidates(world, allCluster, participantIds);
            candidates.Sort((left, right) => CompareCandidate(
                world, click, graphDistance, left, right));
            result.CandidateCount = candidates.Count;
            if (candidates.Count == 0) return result;

            var candidateSet = new HashSet<JunctionId>(candidates);
            var costs = new List<Dictionary<JunctionId, long>>(orderedActors.Count);
            foreach (var actor in orderedActors)
            {
                if (actor.CurrentJunction is not { } start)
                {
                    costs.Add(new Dictionary<JunctionId, long>());
                    continue;
                }

                var avoid = PathfindingSystem.OtherActorJunctions(world, actor);
                var danger = PathfindingSystem.RouteAvoidRing(world, actor);
                var canJump = actor.Body.CanJump && !actor.IsCarryingPerson;
                costs.Add(HexPathfinder.FindCosts(
                    world, start, candidateSet, avoid,
                    PathfindingSystem.ShouldWeightClimbs(actor), canJump,
                    danger, Spec62.DangerStepCost));
            }

            var maximum = MaximumMatching(costs, candidates, candidates.Count);
            if (maximum <= 0) return result;

            var low = 1;
            var high = candidates.Count;
            while (low < high)
            {
                var mid = low + (high - low) / 2;
                if (MaximumMatching(costs, candidates, mid) >= maximum) high = mid;
                else low = mid + 1;
            }

            var matches = MinimumCostMatching(costs, candidates, low, maximum);
            foreach (var pair in matches)
            {
                var actor = orderedActors[pair.actor];
                var destination = candidates[pair.candidate];
                result.Assignments.Add(new Assignment
                {
                    Npc = actor,
                    Destination = destination,
                    PathCost = costs[pair.actor][destination]
                });
            }
            result.Assignments.Sort((a, b) => a.Npc.Id.Value.CompareTo(b.Npc.Id.Value));
            return result;
        }

        /// <summary>Raw seven-tile union; an unobstructed generated world yields 373 ids.</summary>
        public static List<JunctionId> CollectClusterJunctions(WorldState world, Float2 click)
        {
            var target = WorldToTile(click);
            var ids = new HashSet<JunctionId>();
            AddTile(world, target, ids);
            var directions = HexDirection.All;
            for (var i = 0; i < directions.Length; i++)
            {
                AddTile(world, new TileCoord(
                    target.Q + directions[i].DQ, target.R + directions[i].DR), ids);
            }
            return new List<JunctionId>(ids);
        }

        private static void AddTile(WorldState world, TileCoord coord, HashSet<JunctionId> ids)
        {
            if (!world.Tiles.Items.TryGetValue(coord, out var tile)) return;
            foreach (var id in tile.Junctions) ids.Add(id);
        }

        private static TileCoord WorldToTile(Float2 world)
        {
            var r = world.Y / (HexSpatialMath.HexRadius * HexSpatialMath.HexRowStepFactor);
            var q = world.X / (HexSpatialMath.HexRadius * HexSpatialMath.HexWidthFactor) - r * 0.5f;
            var s = -q - r;
            var rq = MathF.Round(q);
            var rr = MathF.Round(r);
            var rs = MathF.Round(s);
            var dq = MathF.Abs(rq - q);
            var dr = MathF.Abs(rr - r);
            var ds = MathF.Abs(rs - s);
            if (dq > dr && dq > ds) rq = -rr - rs;
            else if (dr > ds) rr = -rq - rs;
            return new TileCoord((int)rq, (int)rr);
        }

        private static Dictionary<JunctionId, int> ClusterDistances(
            WorldState world, JunctionId center, List<JunctionId> cluster)
        {
            var allowed = new HashSet<JunctionId>(cluster);
            var distance = new Dictionary<JunctionId, int> { [center] = 0 };
            var queue = new Queue<JunctionId>();
            queue.Enqueue(center);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (!world.Junctions.Items.TryGetValue(current, out var junction)) continue;
                foreach (var neighbor in junction.Neighbors)
                {
                    if (!allowed.Contains(neighbor) || distance.ContainsKey(neighbor)) continue;
                    distance[neighbor] = distance[current] + 1;
                    queue.Enqueue(neighbor);
                }
            }
            return distance;
        }

        private static List<JunctionId> FilterCandidates(
            WorldState world, List<JunctionId> cluster, HashSet<EntityId> participants)
        {
            var occupied = new HashSet<JunctionId>();
            foreach (var npc in world.Entities.Npcs.Values)
            {
                if (!participants.Contains(npc.Id) && npc.Health > 0f &&
                    npc.CurrentJunction is { } standing) occupied.Add(standing);
            }
            foreach (var mob in world.Mobs)
            {
                if (mob.Health > 0f) occupied.Add(mob.Junction);
            }

            var result = new List<JunctionId>();
            foreach (var id in cluster)
            {
                if (!world.Junctions.Items.TryGetValue(id, out var junction) ||
                    junction.Blocked || occupied.Contains(id)) continue;
                if (world.Occupancy.JunctionOwner.TryGetValue(id, out var owner) &&
                    owner is { } occupancyOwner && !participants.Contains(occupancyOwner)) continue;
                if (world.Reservations.Junctions.TryGetValue(id, out var reservation) &&
                    reservation.EndTick >= world.Tick && !participants.Contains(reservation.Owner)) continue;
                result.Add(id);
            }
            return result;
        }

        private static int CompareCandidate(
            WorldState world, Float2 click, Dictionary<JunctionId, int> distance,
            JunctionId left, JunctionId right)
        {
            var ld = distance.TryGetValue(left, out var l) ? l : int.MaxValue;
            var rd = distance.TryGetValue(right, out var r) ? r : int.MaxValue;
            var compare = ld.CompareTo(rd);
            if (compare != 0) return compare;
            var lp = world.Junctions.Items[left].WorldPosition;
            var rp = world.Junctions.Items[right].WorldPosition;
            compare = HexSpatialMath.Distance(lp, click).CompareTo(HexSpatialMath.Distance(rp, click));
            return compare != 0 ? compare : left.Value.CompareTo(right.Value);
        }

        private static int MaximumMatching(
            IReadOnlyList<Dictionary<JunctionId, long>> costs,
            IReadOnlyList<JunctionId> candidates, int prefix)
        {
            var owner = new int[prefix];
            Array.Fill(owner, -1);
            var count = 0;
            for (var actor = 0; actor < costs.Count; actor++)
            {
                var seen = new bool[prefix];
                if (TryMatch(actor, costs, candidates, prefix, owner, seen)) count++;
            }
            return count;
        }

        private static bool TryMatch(
            int actor, IReadOnlyList<Dictionary<JunctionId, long>> costs,
            IReadOnlyList<JunctionId> candidates, int prefix, int[] owner, bool[] seen)
        {
            for (var candidate = 0; candidate < prefix; candidate++)
            {
                if (seen[candidate] || !costs[actor].ContainsKey(candidates[candidate])) continue;
                seen[candidate] = true;
                if (owner[candidate] < 0 ||
                    TryMatch(owner[candidate], costs, candidates, prefix, owner, seen))
                {
                    owner[candidate] = actor;
                    return true;
                }
            }
            return false;
        }

        private sealed class Edge
        {
            public int To;
            public int Reverse;
            public int Capacity;
            public long Cost;
            public int Actor = -1;
            public int Candidate = -1;
        }

        private static List<(int actor, int candidate)> MinimumCostMatching(
            IReadOnlyList<Dictionary<JunctionId, long>> costs,
            IReadOnlyList<JunctionId> candidates, int prefix, int wanted)
        {
            var actorCount = costs.Count;
            var source = 0;
            var actorBase = 1;
            var candidateBase = actorBase + actorCount;
            var sink = candidateBase + prefix;
            var graph = new List<Edge>[sink + 1];
            for (var i = 0; i < graph.Length; i++) graph[i] = new List<Edge>();

            void AddEdge(int from, int to, int capacity, long cost, int actor = -1, int candidate = -1)
            {
                var forward = new Edge
                {
                    To = to, Reverse = graph[to].Count, Capacity = capacity,
                    Cost = cost, Actor = actor, Candidate = candidate
                };
                var reverse = new Edge
                {
                    To = from, Reverse = graph[from].Count, Capacity = 0, Cost = -cost
                };
                graph[from].Add(forward);
                graph[to].Add(reverse);
            }

            for (var actor = 0; actor < actorCount; actor++)
            {
                AddEdge(source, actorBase + actor, 1, 0);
                for (var candidate = 0; candidate < prefix; candidate++)
                {
                    if (!costs[actor].TryGetValue(candidates[candidate], out var pathCost)) continue;
                    var deterministicCost = pathCost * 1_000_000L + actor * 1_000L + candidate;
                    AddEdge(actorBase + actor, candidateBase + candidate, 1,
                        deterministicCost, actor, candidate);
                }
            }
            for (var candidate = 0; candidate < prefix; candidate++)
                AddEdge(candidateBase + candidate, sink, 1, 0);

            var flow = 0;
            while (flow < wanted)
            {
                var inf = long.MaxValue / 4;
                var dist = new long[graph.Length];
                var previousNode = new int[graph.Length];
                var previousEdge = new int[graph.Length];
                var inQueue = new bool[graph.Length];
                for (var i = 0; i < dist.Length; i++)
                {
                    dist[i] = inf;
                    previousNode[i] = -1;
                }
                dist[source] = 0;
                var queue = new Queue<int>();
                queue.Enqueue(source);
                inQueue[source] = true;
                while (queue.Count > 0)
                {
                    var node = queue.Dequeue();
                    inQueue[node] = false;
                    for (var edgeIndex = 0; edgeIndex < graph[node].Count; edgeIndex++)
                    {
                        var edge = graph[node][edgeIndex];
                        if (edge.Capacity <= 0 || dist[node] + edge.Cost >= dist[edge.To]) continue;
                        dist[edge.To] = dist[node] + edge.Cost;
                        previousNode[edge.To] = node;
                        previousEdge[edge.To] = edgeIndex;
                        if (!inQueue[edge.To])
                        {
                            inQueue[edge.To] = true;
                            queue.Enqueue(edge.To);
                        }
                    }
                }

                if (previousNode[sink] < 0) break;
                for (var node = sink; node != source; node = previousNode[node])
                {
                    var from = previousNode[node];
                    var edge = graph[from][previousEdge[node]];
                    edge.Capacity--;
                    graph[node][edge.Reverse].Capacity++;
                }
                flow++;
            }

            var result = new List<(int actor, int candidate)>();
            for (var actor = 0; actor < actorCount; actor++)
            {
                foreach (var edge in graph[actorBase + actor])
                {
                    if (edge.Actor == actor && edge.Candidate >= 0 && edge.Capacity == 0)
                        result.Add((actor, edge.Candidate));
                }
            }
            result.Sort((a, b) => a.actor != b.actor
                ? a.actor.CompareTo(b.actor)
                : a.candidate.CompareTo(b.candidate));
            return result;
        }
    }
}
