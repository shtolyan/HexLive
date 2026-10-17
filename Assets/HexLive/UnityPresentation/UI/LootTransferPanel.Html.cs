#nullable enable
using System.Collections.Generic;
using HexLive.Simulation.Debug;
using HexLive.Simulation.Runtime;
using HexLive.UnityPresentation.Localization;
using HexLive.UnityPresentation.Platform;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace HexLive.UnityPresentation.UI
{
/// <summary>
/// §174.7: the exchange window as the page's «loot» screen. The UI Toolkit panel
/// keeps running hidden — it opens, closes, tracks the order in flight and its
/// status line, and owns every rule; the page names an item (its DragItem, in
/// the uid) and a side, and the panel's own DropOn / ExecuteTransfer /
/// QuickAction act on it.
/// </summary>
public sealed partial class LootTransferPanel
{
    private const string HtmlScreen = "loot";
    private string? _lastHtmlJson;
    private int _htmlTick = -1;
    private bool _htmlDirty;
    private string? _htmlInfoUid;

    private void UpdateHtml()
    {
        if (!IsOpen || !HtmlUi.Active || _runner == null || !_runner.IsReady)
        {
            HideHtml();
            return;
        }

        // The UI Toolkit card stays the owner of the window; it is just not drawn.
        _root.style.visibility = Visibility.Hidden;
        if (!_htmlDirty && _htmlTick == _runner.CurrentTick) return;
        _htmlDirty = false;
        var snapshot = _runner.CreateSnapshot();
        _htmlTick = snapshot.Tick;
        var looter = FindNpc(snapshot, _looterId);
        var builder = CharacterPanel.ItemInformationBuilderFor(_runner);
        if (looter == null || builder == null) return;

        JObject right;
        if (_otherObjectId >= 0)
        {
            var container = FindContainer(snapshot, _otherObjectId);
            if (container == null) return;
            right = ContainerPane(builder, container);
        }
        else
        {
            var other = FindNpc(snapshot, _otherId);
            if (other == null) return;
            right = PersonPane(builder, other);
        }

        var model = new JObject
        {
            ["mode"] = _gift ? "gift" : "exchange",
            ["pending"] = _pending,
            ["status"] = new JObject { ["text"] = _status.text, ["tone"] = StatusTone() },
            ["left"] = PersonPane(builder, looter),
            ["right"] = right,
            ["info"] = InfoModel(snapshot, builder),
        };
        var json = model.ToString(Newtonsoft.Json.Formatting.None);
        if (json == _lastHtmlJson) return;
        _lastHtmlJson = json;
        HtmlUi.Show(HtmlScreen, model);
    }

    private void HideHtml()
    {
        if (_root != null && _root.style.visibility == Visibility.Hidden) _root.style.visibility = StyleKeyword.Null;
        if (_lastHtmlJson == null) return;
        _lastHtmlJson = null;
        _htmlInfoUid = null;
        HtmlUi.Hide(HtmlScreen);
    }

    private string StatusTone()
    {
        if (_pending) return "busy";
        var text = _status.text;
        return text == Loc.Get("loot.transfer_failed") || text == Loc.Get("gift.take_forbidden") ? "error" : "hint";
    }

    // ---- panes: the same projection as BuildInventoryProjection ------------

    private static string Uid(DragItem item) =>
        $"{item.OwnerId}|{(int)item.Source}|{item.Index}|{item.Count}|{item.DefinitionId}";

    private static bool TryParseUid(string? uid, out DragItem item)
    {
        item = default;
        var parts = (uid ?? "").Split(new[] { '|' }, 5);
        if (parts.Length != 5 || !int.TryParse(parts[0], out var owner) || !int.TryParse(parts[1], out var source) ||
            !int.TryParse(parts[2], out var index) || !int.TryParse(parts[3], out var count)) return false;
        item = new DragItem(owner, (InventoryItemSource)source, index, count, parts[4]);
        return true;
    }

    private JObject PersonPane(CharacterPanel builder, NpcSnapshot npc)
    {
        var owner = npc.Id.Value;
        var hands = new List<InventoryContainerSnapshot>();
        InventoryContainerSnapshot? carry = null;
        var regular = new List<InventoryContainerSnapshot>();
        foreach (var container in npc.InventoryContainers)
        {
            if (container.Kind is InventoryContainerKind.HandLeft or InventoryContainerKind.HandRight) hands.Add(container);
            else if (container.Kind == InventoryContainerKind.Carry) carry = container;
            else regular.Add(container);
        }

        var groups = new JArray();
        var representedWorn = new HashSet<int>();
        if (carry != null || hands.Count > 0)
        {
            groups.Add(Group(builder, owner, carry ?? new InventoryContainerSnapshot { Kind = InventoryContainerKind.Carry },
                hands, representedWorn));
        }
        foreach (var container in regular) groups.Add(Group(builder, owner, container, null, representedWorn));

        var worn = new JArray();
        for (var i = 0; i < npc.WornItems.Count; i++)
        {
            if (representedWorn.Contains(i)) continue;
            worn.Add(builder.HtmlItem(Uid(new DragItem(owner, InventoryItemSource.Worn, i, 1, npc.WornItems[i])),
                npc.WornItems[i], 1));
        }

        return new JObject
        {
            ["name"] = Loc.NpcName(npc.DisplayName), ["face"] = builder.HtmlFace(owner), ["kind"] = "person",
            ["groups"] = groups, ["worn"] = worn,
        };
    }

    private JObject Group(CharacterPanel builder, int owner, InventoryContainerSnapshot container,
        IReadOnlyList<InventoryContainerSnapshot>? hands, HashSet<int> representedWorn)
    {
        var slots = new JArray();
        var filled = 0;
        var cap = container.Capacity;
        if (hands != null)
        {
            // The hands lead the carry group: what she holds is what she uses.
            foreach (var hand in hands)
            {
                cap += hand.Capacity;
                var side = hand.Kind == InventoryContainerKind.HandLeft ? "left" : "right";
                foreach (var slot in hand.Slots)
                {
                    if (!string.IsNullOrEmpty(slot.ItemDefinitionId)) filled++;
                    slots.Add(Slot(builder, owner, slot, side));
                }
            }
        }
        foreach (var slot in container.Slots)
        {
            if (!string.IsNullOrEmpty(slot.ItemDefinitionId)) filled++;
            slots.Add(Slot(builder, owner, slot, null));
        }

        JObject? bag = null;
        if (!string.IsNullOrEmpty(container.OwnerItemDefinitionId))
        {
            // The bag itself is a worn item: it can be handed over too.
            representedWorn.Add(container.OwnerSourceIndex);
            if (container.OwnerSourceIndex >= 0)
            {
                bag = builder.HtmlItem(Uid(new DragItem(owner, InventoryItemSource.Worn, container.OwnerSourceIndex, 1,
                    container.OwnerItemDefinitionId)), container.OwnerItemDefinitionId, 1);
            }
        }

        return new JObject
        {
            ["title"] = ContainerTitle(container), ["filled"] = filled, ["cap"] = cap,
            ["overflow"] = container.Kind == InventoryContainerKind.Overflow, ["owner"] = bag, ["slots"] = slots,
        };
    }

    private static JObject Slot(CharacterPanel builder, int owner, InventorySlotSnapshot slot, string? hand,
        bool useCellIndex = false)
    {
        JObject? item = null;
        var index = useCellIndex ? slot.Index : slot.SourceIndex;
        if (!string.IsNullOrEmpty(slot.ItemDefinitionId) && index >= 0)
        {
            var count = Mathf.Max(1, slot.StackCount);
            item = builder.HtmlItem(Uid(new DragItem(owner, InventoryItemSource.Carried, index, count, slot.ItemDefinitionId)),
                slot.ItemDefinitionId, count);
        }
        return new JObject { ["item"] = item, ["hand"] = hand };
    }

    /// <summary>§128.5: a thing that holds things — one flat group, no anatomy.</summary>
    private JObject ContainerPane(CharacterPanel builder, ObjectSnapshot container)
    {
        var groups = new JArray();
        if (container.Contents.Count > 0 || container.ContainerCapacity > 0)
        {
            var slots = new JArray();
            foreach (var slot in container.Contents)
                slots.Add(Slot(builder, -container.Id.Value, slot, null, useCellIndex: true));
            for (var i = container.Contents.Count; i < container.ContainerCapacity; i++)
                slots.Add(new JObject { ["item"] = null, ["hand"] = null });
            groups.Add(new JObject
            {
                ["title"] = ItemName(container.DefinitionId), ["filled"] = container.Contents.Count,
                ["cap"] = Mathf.Max(container.ContainerCapacity, container.Contents.Count), ["overflow"] = false,
                ["owner"] = null, ["slots"] = slots,
            });
        }
        return new JObject
        {
            ["name"] = ItemName(container.DefinitionId), ["face"] = null, ["kind"] = "container",
            ["groups"] = groups, ["worn"] = new JArray(),
        };
    }

    // ---- the item card --------------------------------------------------------

    /// <summary>The card of the item the page asked about, resolved as ShowItemInformation does.</summary>
    private JObject? InfoModel(WorldSnapshot snapshot, CharacterPanel builder)
    {
        if (_htmlInfoUid == null || !TryParseUid(_htmlInfoUid, out var item)) return null;
        NpcSnapshot? owner = null;
        InventorySlotSnapshot? slot = null;
        var worn = item.Source == InventoryItemSource.Worn;
        if (item.OwnerId >= 0)
        {
            owner = FindNpc(snapshot, item.OwnerId);
            if (owner == null) return null;
            if (worn)
            {
                if (item.Index < 0 || item.Index >= owner.WornItems.Count || owner.WornItems[item.Index] != item.DefinitionId) return null;
            }
            else
            {
                foreach (var container in owner.InventoryContainers)
                    foreach (var candidate in container.Slots)
                        if (candidate.SourceIndex == item.Index && candidate.ItemDefinitionId == item.DefinitionId) slot = candidate;
                if (slot == null) return null;
            }
        }
        else
        {
            var container = FindContainer(snapshot, -item.OwnerId);
            if (container == null) return null;
            foreach (var candidate in container.Contents)
                if (candidate.Index == item.Index && candidate.ItemDefinitionId == item.DefinitionId) slot = candidate;
            if (slot == null) return null;
        }

        return new JObject
        {
            ["uid"] = _htmlInfoUid,
            ["detail"] = builder.HtmlItemInformation(item.DefinitionId, owner!, worn, item.Index,
                slot?.StackCount ?? 1, item.OwnerId < 0 ? slot : null),
        };
    }

    // ---- commands -------------------------------------------------------------

    private void OnHtmlCommand(string command, JObject args)
    {
        if (!IsOpen) return; // queued before Escape closed the window
        _htmlDirty = true;
        switch (command)
        {
            case "close":
                Hide();
                return;
            case "info":
                _htmlInfoUid = (string?)args["uid"];
                return;
            case "quick":
                if (TryParseUid((string?)args["uid"], out var quick))
                {
                    _statusHeld = false;
                    QuickAction(quick);
                }
                return;
            case "move":
                if (_pending || !TryParseUid((string?)args["uid"], out var item)) return;
                var destination = (string?)args["to"] == "left" ? _looterId : _otherId;
                if (destination == item.OwnerId) return;
                _statusHeld = false;
                // DropOn's own rules (one-way gift), but the page already asked how many.
                if (_gift && destination == _looterId)
                {
                    _statusHeld = true;
                    _status.text = Loc.Get("gift.take_forbidden");
                    return;
                }
                ExecuteTransfer(destination, item, Mathf.Clamp((int?)args["count"] ?? item.Count, 1, item.Count));
                return;
        }
    }
}
}
