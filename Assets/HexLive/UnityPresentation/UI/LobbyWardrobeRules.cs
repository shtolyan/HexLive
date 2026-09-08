using System;
using System.Collections.Generic;
using System.Linq;

namespace HexLive.UnityPresentation.UI
{
// §162: the editor evaluates the exact slots/layers published by the selected server.
public static class LobbyWardrobeRules
{
    public sealed class Item
    {
        public string Id, Prototype, Sex, Layer;
        public string[] Slots = Array.Empty<string>(), Covers = Array.Empty<string>();
        public bool AuthoredSlots;
        public string Model => string.IsNullOrEmpty(Prototype) ? Id : Prototype;
        public bool Compatible(bool male) => Sex == "Any" || Sex == (male ? "Male" : "Female");
    }
    public static string Conflict(IEnumerable<Item> catalog, IReadOnlyCollection<string> worn, Item selected)
    {
        return catalog.FirstOrDefault(item => worn.Contains(item.Id) && item.Model != selected.Model &&
            item.Layer == selected.Layer &&
            (item.AuthoredSlots && selected.AuthoredSlots
                ? item.Slots.Intersect(selected.Slots).Any()
                : item.Covers.Intersect(selected.Covers).Any()))?.Id;
    }
    public static bool Equip(IReadOnlyList<Item> catalog, List<string> worn, Item selected, out string conflict)
    {
        conflict = Conflict(catalog, worn, selected);
        if (conflict != null) return false;
        var variants = catalog.Where(item => item.Model == selected.Model).Select(item => item.Id).ToHashSet();
        worn.RemoveAll(variants.Contains);
        worn.Add(selected.Id);
        return true;
    }
    public static string[] RemoveIncompatible(IReadOnlyList<Item> catalog, List<string> worn, bool male)
    {
        var compatible = catalog.Where(item => item.Compatible(male)).Select(item => item.Id).ToHashSet();
        var removed = worn.Where(id => !compatible.Contains(id)).ToArray();
        worn.RemoveAll(id => !compatible.Contains(id));
        return removed;
    }
}
}
