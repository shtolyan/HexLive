using HexLive.Simulation.Core;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Spatial;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Memory;
using HexLive.Simulation.Social;

namespace HexLive.Simulation.Runtime
{

public sealed partial class DecisionSystem
{
    // Spec 35.3: materials bill for the pending hut piece.
    internal readonly struct BuildPiece
    {
        public BuildPiece(int logs, int stones, int leaves, int edge, string kind)
        {
            Logs = logs;
            Stones = stones;
            Leaves = leaves;
            Edge = edge;
            Kind = kind;
        }

        public int Logs { get; }

        public int Stones { get; }

        public int Leaves { get; }

        public int Edge { get; }

        public string Kind { get; }
    }

    internal static BuildPiece? NextBuildPiece(WorldState world)
    {
        var project = world.Project;
        if (project is null || project.Completed)
        {
            return null;
        }

        if (!project.FloorDone)
        {
            return new BuildPiece(1, 0, 2, -1, "Floor");
        }

        for (var i = 0; i < 6; i++)
        {
            if (i != project.DoorEdge && !project.EdgeDone[i])
            {
                return new BuildPiece(1, 1, 0, i, "Wall");
            }
        }

        if (!project.EdgeDone[project.DoorEdge])
        {
            return new BuildPiece(2, 0, 0, project.DoorEdge, "Door");
        }

        return null;
    }

    // Spec §52: the nearest reachable, unfinished furniture build-site the NPC
    // can see (live object, so its Contents/Bill are readable).
    internal static WorldObjectState FindBuildSite(NPCState npc, WorldState world)
    {
        // Spec §54: the hearth build-site wins over any other site — it only
        // exists during cold start (until the campfire is raised), and it must
        // be built first (fire gates warmth, cooking and all crafting).
        //
        // Behavior audit (Jul 2026): after the hearth, the queue is ORDERED,
        // not first-perceived — the live campfire's open upgrade bill (12
        // sticks + 18 stones + 2 rope) used to hijack the single buildSite
        // slot for the whole run, so the bed's rope stage was never "the
        // site's need" and CraftRope fired 0 times in 250 soak-days. Sleep
        // furniture (bed, drying rack) finishes first; the stone ring and
        // other upgrades take the surplus afterwards.
        // §64.9: the colony SPLITS its builders instead of queueing them. The
        // single buildSite slot is what every bed feeder reads (dreamPull and
        // bedLeaf/Stick/Rope/LogPull all require "the slot IS the bed"), so
        // whatever holds the slot is the only thing that colonist can build.
        // Measured on the 6-seed 10-day soak before this change: seed 31337 held
        // campfire.spot@upgrade for 78 984 npc-ticks and the staked bed.leaf for
        // 244 — the bed's stage-1 sticks never got a single pull, and 0 beds were
        // raised on ALL six seeds (30-day baseline: 2 beds for 24 colonists).
        //
        // A plain "dream first" rank fixes the beds and KILLS the colony: the
        // hearth's stone ring and the water collector sat behind a bed queue that
        // lasts the whole run, and the 30-day soak went 4/4 alive → whole-colony
        // wipes on seeds 777/999 (thirst 1.00, thermal −0.5…−0.79). Strict
        // priority in either direction starves whatever is behind it.
        //
        // So: dream builders take the bed, the rest keep the §63 r2 order
        // (hearth upgrade > furniture > the rest) untouched. Labour is split, no
        // project is starved, and who builds beds is a stable per-girl trait.
        // §72: the colony's shared aspiration for a colonist; for a lone
        // outsider there is no labour to split, so his own dream is the gate.
        var activeDream = npc.Faction == Faction.Colony
            ? world.ActiveDream
            : npc.Mind.CurrentDream;
        var buildsTheDream = SpecDream.Enabled &&
            activeDream == DreamType.OwnBed &&
            IsDreamBuilder(npc, world);
        WorldObjectState firstSite = null;
        WorldObjectState houseSite = null;
        WorldObjectState dreamSite = null;
        WorldObjectState collectorSite = null;
        WorldObjectState furnitureSite = null;
        WorldObjectState hearthUpgrade = null;
        // §54.19: стройка, которую нечем закрыть, — последняя в очереди, а не
        // первая. Не выбрасывается: если материала нет НИ У ОДНОЙ, колония
        // по-прежнему берёт что было и ведёт себя как раньше.
        WorldObjectState starvedSite = null;
        // Что колония вообще может достать — считается ОДНИМ проходом по
        // восприятию и только если понадобится. Наивные шесть-восемь
        // `HasReachableWithTag` на каждую стройку означали бы сорок проходов по
        // списку из сотен предметов на КАЖДЫЙ выбор цели.
        MaterialSources sources = default;
        var sourcesScanned = false;
        foreach (var obj in npc.Perception.Objects)
        {
            if (!obj.IsReachable ||
                !world.Entities.Objects.TryGetValue(obj.Id, out var site) ||
                !BuildSiteMath.IsSite(site) ||
                !IsOurSite(world, npc, site))
            {
                continue;
            }

            // §54.14: only the BARE hearth site (no fire raised yet) gets the
            // cold-start priority. A live campfire mid-upgrade (stone ring /
            // spit outstanding) queues like any other furniture site.
            //
            // §54.19 не трогает эту ветку сознательно: очаг — контракт
            // холодного старта (§54.6), его стадия 1 это палки, а палок в мире
            // нет только тогда, когда не построить вообще ничего.
            if (site.BuildProduct == ContentIds.Campfire && site.DefinitionId == ContentIds.BuildSite)
            {
                return site;
            }

            // §54.19: метка «нечем закрыть» — колониальная, а не личная: её
            // снимает ЛЮБАЯ девушка, которая видит источник, и ставит она себя
            // сама, один раз. Значит «мёртвая» = мёртвая для всех подряд
            // BuildSiteUnstockableSkipTicks тиков, а не «эта отвернулась».
            if (SiteShortOfAnything(site))
            {
                if (!sourcesScanned)
                {
                    sources = MaterialSources.Scan(world, npc);
                    sourcesScanned = true;
                }

                if (SiteStockableNow(site, sources, npc))
                {
                    site.UnstockableSinceTick = null;
                }
                else
                {
                    site.UnstockableSinceTick ??= world.Tick;
                }
            }
            else
            {
                site.UnstockableSinceTick = null; // всё привезли, ждёт молотка
            }

            if (site.UnstockableSinceTick is { } since &&
                world.Tick - since >= SimBalance.BuildSiteUnstockableSkipTicks)
            {
                starvedSite ??= site;
                continue;
            }

            if (site.DefinitionId == ContentIds.Campfire)
            {
                hearthUpgrade ??= site;
            }
            else if (site.DefinitionId == ContentIds.BuildSite &&
                site.BuildProduct == ContentIds.BedBasic)
            {
                if (buildsTheDream)
                {
                    dreamSite ??= site;
                }
                else
                {
                    furnitureSite ??= site;
                }
            }
            else if (BuildSiteMath.IsArchitecturalBuilding(site.BuildProduct) ||
                     BuildSiteMath.IsFreeArchitectureSite(site))
            {
                // §120: a HOUSE is the colony's shelter, not a comfort upgrade.
                // Left as the unranked `firstSite` fallback it is starved
                // outright: the single buildSite slot goes to whatever else is
                // staked, and a personal bed waiting on logs that this island
                // does not have holds that slot forever. Measured in the §120
                // sandbox (seed 12345, 40 000 ticks): the staked house took
                // 0 of 151 sticks while a bed.basic site sat at 0/4 logs.
                //
                // This lane is inert in the shipped game — nothing stakes an
                // architectural site there — so it can only change worlds that
                // deliberately put a house up.
                houseSite ??= site;
            }
            else if (site.DefinitionId == ContentIds.BuildSite &&
                site.BuildProduct == ContentIds.WaterCollector)
            {
                // §54.15: survival infrastructure has a bounded queue. The
                // first collector must not sit behind every personal bed or
                // the hearth's comfort upgrades; without it the renewable
                // water branch never exists at all.
                collectorSite ??= site;
            }
            else if (site.DefinitionId == ContentIds.BuildSite &&
                site.BuildProduct is ContentIds.DryingRack or ContentIds.Workbench)
            {
                furnitureSite ??= site;
            }

            firstSite ??= site;
        }

        // The bare first hearth returned immediately above. Once fire exists,
        // finish the one renewable-water station before comfort upgrades and
        // personal dreams; after it is raised it is no longer a site and the
        // established queue resumes unchanged.
        // A fresh carcass is a perishable survival opportunity: if someone is
        // already carrying raw meat, finish the hearth's spit before the
        // collector queue so the butchered calories do not stall in a pack.
        var needsSpitNow = npc.Inventory.Items.Contains(ContentIds.MeatRaw);
        return needsSpitNow && hearthUpgrade != null
            ? hearthUpgrade
            : collectorSite ?? houseSite ?? dreamSite ?? hearthUpgrade ?? furnitureSite ??
              firstSite ?? starvedSite;
    }

    /// <summary>§54.19: ждёт ли стройка ещё хоть чего-нибудь на ТЕКУЩЕЙ стадии.
    /// Дешёвая проверка перед дорогой — стройке, которой всё привезли, вопрос
    /// «можно ли её закрыть» задавать незачем.</summary>
    private static bool SiteShortOfAnything(WorldObjectState site)
    {
        foreach (var material in BuildSiteMath.AllMaterials)
        {
            if (BuildSiteMath.Remaining(site, material) > 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// §54.19: можно ли ТЕКУЩУЮ стадию этой стройки вообще чем-то закрыть —
    /// есть ли для каждого недостающего материала хоть какой-то источник.
    /// </summary>
    private static bool SiteStockableNow(
        WorldObjectState site, in MaterialSources sources, NPCState npc)
    {
        foreach (var material in BuildSiteMath.AllMaterials)
        {
            if (BuildSiteMath.Remaining(site, material) <= 0)
            {
                continue;
            }

            // Несёт сама — донесёт.
            if (npc.Inventory.Items.Contains(material))
            {
                continue;
            }

            if (!sources.Has(material))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// §54.19: что из строительных материалов колонии вообще доступно — один
    /// проход по восприятию вместо восьми запросов по тегу.
    /// <para>
    /// Список источников нарочно ЩЕДРЫЙ: цена ложного «нет» (живую стройку
    /// задвинули в конец очереди) выше цены ложного «да» (всё как раньше).
    /// Поэтому считаются и производные пути: палка = лежит ИЛИ раскалывается из
    /// бревна, верёвка = лежит ИЛИ вьётся из волокна, лист = лежит ИЛИ
    /// срубается с кроны.
    /// </para>
    /// <para>
    /// ⭐ У БРЕВНА производного пути нет — и это не упущение. §64.9 запретил
    /// валить пальмы под счёт постройки (пальма — вода колонии), так что
    /// лежащее бревно единственный источник; именно поэтому кровать с
    /// «log 0/4» и была вечным жильцом слота очереди.
    /// </para>
    /// </summary>
    private readonly struct MaterialSources
    {
        private MaterialSources(bool log, bool stone, bool boulder, bool leaf, bool crown,
            bool palm, bool stick, bool rope, bool fiber, bool board)
        {
            Log = log;
            Stone = stone;
            Boulder = boulder;
            Leaf = leaf;
            Crown = crown;
            Palm = palm;
            Stick = stick;
            Rope = rope;
            Fiber = fiber;
            Board = board;
        }

        private bool Log { get; }

        private bool Stone { get; }

        private bool Boulder { get; }

        private bool Leaf { get; }

        private bool Crown { get; }

        private bool Palm { get; }

        private bool Stick { get; }

        private bool Rope { get; }

        private bool Fiber { get; }

        private bool Board { get; }

        public static MaterialSources Scan(WorldState world, NPCState npc)
        {
            bool log = false, stone = false, boulder = false, leaf = false, crown = false;
            bool palm = false, stick = false, rope = false, fiber = false, board = false;
            foreach (var obj in npc.Perception.Objects)
            {
                // Те же фильтры, что у HasReachableWithTag: недостижимое,
                // занятое и отвергнутое памятью источником не является.
                if (!obj.IsReachable || !ObjectUsableBy(obj, npc.Id) ||
                    npc.Memory.IsShunned(obj.Id, world.Tick) ||
                    !world.Content.ObjectDefinitions.TryGetValue(
                        obj.DefinitionId, out var definition))
                {
                    continue;
                }

                foreach (var tag in definition.Tags)
                {
                    switch (tag)
                    {
                        case ObjectTags.Log: log = true; break;
                        case ObjectTags.Stone: stone = true; break;
                        case ObjectTags.Boulder: boulder = true; break;
                        case ObjectTags.PalmLeaf: leaf = true; break;
                        case ObjectTags.PalmCrown: crown = true; break;
                        case ObjectTags.Palm: palm = true; break;
                        case ObjectTags.Stick: stick = true; break;
                        case ObjectTags.Rope: rope = true; break;
                        case ObjectTags.Fiber:
                        case ObjectTags.Yucca: fiber = true; break;
                    }
                }

                if (obj.DefinitionId == ContentIds.Board)
                {
                    board = true;
                }
            }

            return new MaterialSources(log, stone, boulder, leaf, crown, palm, stick, rope,
                fiber, board);
        }

        public bool Has(string material) => material switch
        {
            BuildSiteMath.MaterialLogs => Log,
            BuildSiteMath.MaterialStones => Stone || Boulder,
            BuildSiteMath.MaterialLeaves => Leaf || Crown || Palm,
            BuildSiteMath.MaterialSticks => Stick || Log,
            BuildSiteMath.MaterialRope => Rope || Fiber,
            BuildSiteMath.MaterialBoards => Board || Log,
            _ => true
        };
    }

    // §80: своя ли это стройка. §72 развёл лагеря, но очередь построек — нет:
    // чужак мог взять в работу очаг колонии, а девушка — его стоянку, и оба
    // таскали бы материалы врагу. Раньше не стреляло только потому, что своя
    // стоянка помнится постоянно и почти всегда оказывалась первой.
    //
    // Мера — чей лагерь ближе. Владелец (Owner) есть не у всякой стройки:
    // общий очаг колонии ничей, поэтому по владельцу одному судить нельзя.
    // Если фракционных домов в мире нет вовсе (старый сейв, тестовый мир) —
    // ограничение не применяется, поведение остаётся прежним.
    //
    // Internal (§72.13): планировщик обязан задавать ТОТ ЖЕ вопрос. Аукцион ставит цель
    // по своей стройке (FindBuildSite), но план брал БЛИЖАЙШИЙ подходящий
    // объект из восприятия — и девушка с палками в руках, проходя мимо чужой
    // стоянки, достраивала её (см. IsValidTargetFor: Build/BuildFurniture).
    internal static bool IsOurSite(WorldState world, NPCState npc, WorldObjectState site)
    {
        if (site.Owner is { } owner)
        {
            return world.Entities.Npcs.TryGetValue(owner, out var builder) &&
                   FactionRelations.AreAllies(npc.Faction, builder.Faction);
        }

        var ours = int.MaxValue;
        var theirs = int.MaxValue;
        foreach (var pair in world.FactionHomes)
        {
            var distance = HexSpatialMath.HexDistance(site.Tile, pair.Value);
            if (FactionRelations.AreAllies(npc.Faction, pair.Key))
            {
                ours = System.Math.Min(ours, distance);
            }
            else
            {
                theirs = System.Math.Min(theirs, distance);
            }
        }

        return ours <= theirs;
    }

    // §64.9: is this colonist one of the colony's bed builders? Her own staked
    // site always is (nobody else owes her a bed), plus a stable share of the
    // others so the bed is worked by a crew, not by one girl between chores.
    // Deterministic per girl-per-world (Hash01 over seed+id), so the split
    // survives reloads and reads as character rather than as flicker.
    private static bool IsDreamBuilder(NPCState npc, WorldState world)
    {
        foreach (var obj in world.Entities.Objects.Values)
        {
            if (obj.BuildProduct == ContentIds.BedBasic &&
                obj.Owner is { } owner && owner.Equals(npc.Id))
            {
                return true;
            }
        }

        return MathUtil.Hash01(world.Seed, npc.Id.Value, 64, 6409) <
            SpecDream.DreamBuilderShare;
    }

    internal static bool CarriesSiteMaterial(NPCState npc, WorldObjectState site)
    {
        foreach (var mat in BuildSiteMath.AllMaterials)
        {
            if (BuildSiteMath.AcceptsDelivery(site, mat) && npc.Inventory.Items.Contains(mat))
            {
                return true;
            }
        }

        return false;
    }
}

}
