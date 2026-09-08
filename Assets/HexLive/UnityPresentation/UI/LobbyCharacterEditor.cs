using System;
using System.Collections.Generic;
using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Content;
using HexLive.UnityPresentation.Localization;
using HexLive.UnityPresentation.Wearing.Garments;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace HexLive.UnityPresentation.UI
{
// §162: UI state is independent of the live actor and of the persisted creation data.
public sealed class LobbyCharacterEditor : IDisposable
{
    private static readonly string[] Tabs = { "body", "skin", "eyes", "hair", "underwear", "wear", "outerwear", "bags" };
    private static readonly string[] SkinOrder = { "Molly", "Marta", "Jana", "Jolly" };
    private readonly CharacterCreationConfig _npc;
    private readonly JObject _catalog;
    private readonly LobbyWardrobeRules.Item[] _wardrobe;
    private readonly Action _save, _random;
    private readonly Action<string> _message;
    private readonly LobbyCharacterPreview _preview;
    private readonly LobbyAppearanceThumbnails _thumbnails;
    private readonly VisualElement _appearance, _parameters, _variants;
    private readonly ScrollView _grid;
    private readonly TextField _search;
    private readonly Dictionary<string, Vector2> _scroll = new();
    private readonly Dictionary<string, string> _searches = new(), _selections = new();
    private readonly List<(Button button, Func<bool> selected)> _tiles = new();
    private readonly List<(Button button, string model)> _models = new();
    private readonly Dictionary<string, Button> _tabs = new();
    private string _tab = "body";
    private bool _disposed;
    private static string T(string key) => Loc.Get("lobby." + key);
    private IEnumerable<string> Values(string key) => _catalog[key]?.Values<string>() ?? Enumerable.Empty<string>();
    private IEnumerable<JToken> Clothes => _catalog["clothing"] ?? Enumerable.Empty<JToken>();
    private static bool Male(string body) => body is "Kshishtof" or "Tonny";
    private bool Compatible(JToken item) => item.Value<string>("sex") == "Any" || item.Value<string>("sex") == (Male(_npc.Body) ? "Male" : "Female");
    private static string Id(JToken item) => item.Value<string>("id");
    private static string Model(JToken item) => string.IsNullOrEmpty(item.Value<string>("prototypeId")) ? Id(item) : item.Value<string>("prototypeId");

    public LobbyCharacterEditor(VisualElement parent, MonoBehaviour runner, CharacterCreationConfig npc,
        WorldCreationConfig world, JObject catalog, Action save, Action random, Action done, Action<string> message,
        Func<Faction, string> campName)
    {
        _npc = npc; _catalog = catalog; _save = save; _random = random; _message = message;
        _wardrobe = Clothes.Select(g => new LobbyWardrobeRules.Item
        {
            Id = Id(g), Prototype = g.Value<string>("prototypeId"), Sex = g.Value<string>("sex"), Layer = g.Value<string>("layer"),
            Slots = g["slots"].Values<string>().ToArray(), Covers = g["covers"].Values<string>().ToArray(), AuthoredSlots = g.Value<bool>("authoredSlots")
        }).ToArray();
        var columns = Panel(parent, "lobby-columns");
        var left = Panel(columns, "lobby-preview");
        var name = new TextField(T("characterName")) { value = Loc.NpcName(npc.Name), name = "characterName" }; left.Add(name);
        name.RegisterValueChangedCallback(e => { npc.Name = e.newValue; _save(); });
        var camps = world.Camps.Select(c => c.Faction.ToString()).ToList();
        var camp = new DropdownField(T("camp"), camps, camps.IndexOf(npc.Camp.ToString()),
            v => campName(Enum.Parse<Faction>(v)), v => campName(Enum.Parse<Faction>(v))); left.Add(camp);
        camp.RegisterValueChangedCallback(e => { npc.Camp = Enum.Parse<Faction>(e.newValue); npc.Controlled &= npc.Camp == world.PlayerCamp; _save(); });
        _preview = new LobbyCharacterPreview(left, runner, npc);
        _thumbnails = new LobbyAppearanceThumbnails(runner);
        var right = Panel(columns, "lobby-editor");
        var modes = Panel(right, "lobby-modes");
        Button appearance = null, parameters = null;
        appearance = AddButton(modes, T("appearance"), () => Mode(true));
        parameters = AddButton(modes, T("parameters"), () => Mode(false));
        void Mode(bool showAppearance)
        {
            _appearance.EnableInClassList("lobby-hidden", !showAppearance);
            _parameters.EnableInClassList("lobby-hidden", showAppearance);
            appearance.EnableInClassList("lobby-selected", showAppearance); parameters.EnableInClassList("lobby-selected", !showAppearance);
        }
        _appearance = Panel(right, "lobby-appearance");
        var tabs = Panel(_appearance, "lobby-tabs");
        foreach (var tab in Tabs)
            _tabs[tab] = AddButton(tabs, T(tab is "body" or "skin" or "eyes" or "hair" ? tab : "layer." + tab), () => SwitchTab(tab));
        _search = new TextField(T("search")); _appearance.Add(_search);
        _search.RegisterValueChangedCallback(e => { _searches[_tab] = e.newValue; _scroll[_tab] = Vector2.zero; BuildGrid(); });
        _grid = new ScrollView(); _grid.AddToClassList("lobby-catalog"); _grid.contentContainer.AddToClassList("lobby-tile-grid"); _appearance.Add(_grid);
        _variants = Panel(_appearance, "lobby-variants");
        _parameters = new ScrollView(); _parameters.AddToClassList("lobby-parameters"); right.Add(_parameters);
        BuildParameters();
        var footer = Panel(parent, "lobby-toolbar");
        AddButton(footer, T("random"), () => { _random(); Changed(); BuildGrid(); BuildParameters(); });
        AddButton(footer, T("done"), done);
        Mode(true); BuildGrid();
    }
    private static VisualElement Panel(VisualElement parent, string css)
    { var panel = new VisualElement(); panel.AddToClassList(css); parent.Add(panel); return panel; }
    private static Button AddButton(VisualElement parent, string text, Action action)
    { var button = new Button(action) { text = text }; parent.Add(button); return button; }
    private void Changed() { _save(); _preview.Refresh(_npc); Highlight(); }
    private void Highlight()
    {
        foreach (var (button, selected) in _tiles) button.EnableInClassList("lobby-selected", selected());
        foreach (var (button, model) in _models) button.EnableInClassList("lobby-focused", _selections.GetValueOrDefault(_tab) == model);
    }
    private void SwitchTab(string tab)
    {
        if (_tab == tab) return;
        _scroll[_tab] = _grid.scrollOffset; _tab = tab;
        _search.SetValueWithoutNotify(_searches.GetValueOrDefault(tab, "")); BuildGrid();
    }
    private string BodyName(string id)
    {
        if (!Male(id)) return T("body." + id.ToLowerInvariant());
        var bodies = Values("bodies").Where(Male).ToList();
        return T("body.male") + (bodies.Count > 1 ? " " + (bodies.IndexOf(id) + 1) : "");
    }
    private static string ClothingName(JToken item)
    {
        var key = ItemCatalog.Resolve(Id(item)).NameKey;
        return Loc.Has(key) ? Loc.Get(key) : item.Value<string>("name") ?? Id(item);
    }
    private string HairName(string id)
    {
        if (string.IsNullOrEmpty(id)) return T("authored");
        if (id == "none") return T("none");
        var list = _catalog["hair"].Select(Id).OrderBy(v => v, StringComparer.Ordinal).ToList();
        return string.Format(T("hairNumber"), list.IndexOf(id) + 1);
    }
    private CharacterCreationConfig Look(string body = null, string skin = null, string eyes = null, string hair = null, string colour = null)
    {
        return new CharacterCreationConfig { Id = _npc.Id, Body = body ?? _npc.Body, Skin = skin ?? _npc.Skin, Eyes = eyes ?? _npc.Eyes,
            Hair = hair ?? _npc.Hair, HairColour = colour ?? _npc.HairColour, Voice = _npc.Voice, Clothing = new List<string>() };
    }
    private Button Tile(VisualElement container, string title, Func<bool> selected, Action choose,
        CharacterCreationConfig look = null, bool head = false, string iconId = null)
    {
        if (container == _grid.contentContainer && !string.IsNullOrWhiteSpace(_search.value) && title.IndexOf(_search.value, StringComparison.OrdinalIgnoreCase) < 0) return null;
        var button = new Button(choose) { tooltip = title }; button.AddToClassList("lobby-tile"); container.Add(button);
        var image = new Image { scaleMode = ScaleMode.ScaleToFit, pickingMode = PickingMode.Ignore }; image.AddToClassList("lobby-tile-image"); button.Add(image);
        button.Add(new Label(title) { pickingMode = PickingMode.Ignore });
        _tiles.Add((button, selected));
        if (iconId != null)
        {
            IVisualElementScheduledItem request = null;
            request = image.schedule.Execute(() =>
            {
                if (_disposed || !Visible(image)) return;
                image.sprite = ItemIcons.Load(iconId); if (image.sprite != null) request.Pause();
            }).Every(250);
        }
        else if (look != null)
        {
            var framing = head ? (_tab == "eyes" ? 2 : 1) : 0;
            image.schedule.Execute(() =>
            {
                if (_disposed || !Visible(image)) return;
                var texture = _thumbnails.GetOrRequest(look, framing);
                if (texture != null) image.image = texture;
            }).Every(200);
        }
        return button;
    }
    private static bool Visible(VisualElement element)
    {
        if (element.panel == null || element.resolvedStyle.display == DisplayStyle.None) return false;
        for (var p = element.parent; p != null; p = p.parent)
            if (p.resolvedStyle.display == DisplayStyle.None || (p is ScrollView scroll && !scroll.contentViewport.worldBound.Overlaps(element.worldBound))) return false;
        return true;
    }
    private void BuildGrid()
    {
        _tiles.Clear(); _models.Clear(); _grid.Clear(); _variants.Clear();
        foreach (var (key, button) in _tabs) button.EnableInClassList("lobby-selected", key == _tab);
        switch (_tab)
        {
            case "body":
                foreach (var id in Values("bodies"))
                    Tile(_grid.contentContainer, BodyName(id), () => _npc.Body == id, () => ChangeBody(id),
                        Look(body: id, skin: Male(id) ? id : SkinOrder.Contains(_npc.Skin) ? _npc.Skin : id, eyes: Male(id) ? "" : _npc.Eyes, hair: "none"));
                break;
            case "skin":
                foreach (var id in Male(_npc.Body) ? new[] { _npc.Body } : Values("skins"))
                    Tile(_grid.contentContainer, Male(_npc.Body) ? T("authored") : string.Format(T("skinNumber"), Array.IndexOf(SkinOrder, id) + 1), () => _npc.Skin == id,
                        () => { _npc.Skin = id; Changed(); }, Look(skin: id, hair: "none"), true);
                break;
            case "eyes":
                foreach (var id in Male(_npc.Body) ? new[] { "" } : Values("eyes"))
                    Tile(_grid.contentContainer, string.IsNullOrEmpty(id) ? T("authored") : T("eye." + id), () => _npc.Eyes == id,
                        () => { _npc.Eyes = id; Changed(); }, Look(eyes: id, hair: "none"), true);
                break;
            case "hair":
                foreach (var id in Male(_npc.Body) ? new[] { "", "none" } : new[] { "none" }.Concat(_catalog["hair"].Select(Id)))
                    Tile(_grid.contentContainer, HairName(id), () => _npc.Hair == id,
                        () => { if (_npc.Hair != id) { _npc.Hair = id; _npc.HairColour = "prototype"; Changed(); } _selections[_tab] = id; BuildVariants(); }, Look(hair: id, colour: "prototype"), true);
                break;
            default:
                foreach (var group in Clothes.Where(Compatible).Where(g => string.Equals(g.Value<string>("layer"), _tab, StringComparison.OrdinalIgnoreCase)).GroupBy(Model))
                {
                    var item = group.FirstOrDefault(g => _npc.Clothing.Contains(Id(g))) ?? group.First(); var model = group.Key;
                    var prototype = group.FirstOrDefault(g => Id(g) == model) ?? group.First();
                    var tile = Tile(_grid.contentContainer, ClothingName(prototype), () => Clothes.Any(g => Model(g) == model && _npc.Clothing.Contains(Id(g))),
                        () => { _selections[_tab] = model; BuildVariants(); }, iconId: Id(item));
                    if (tile != null) _models.Add((tile, model));
                }
                break;
        }
        BuildVariants(); Highlight();
        var offset = _scroll.GetValueOrDefault(_tab, Vector2.zero);
        _grid.schedule.Execute(() => _grid.scrollOffset = offset);
    }
    private void ChangeBody(string body)
    {
        if (_npc.Body == body) return;
        var removed = new List<string>();
        _npc.Body = body;
        if (Male(body))
        {
            if (_npc.Skin != body) removed.Add(T("skin")); _npc.Skin = body;
            if (!string.IsNullOrEmpty(_npc.Eyes)) removed.Add(T("eyes")); _npc.Eyes = "";
            if (_npc.Hair != "none" && !string.IsNullOrEmpty(_npc.Hair)) { removed.Add(T("hair")); _npc.Hair = "none"; }
            _npc.HairColour = "prototype";
        }
        else
        {
            if (!Values("skins").Contains(_npc.Skin)) { removed.Add(T("skin")); _npc.Skin = Values("skins").Contains(body) ? body : Values("skins").FirstOrDefault() ?? body; }
            if (!string.IsNullOrEmpty(_npc.Eyes) && !Values("eyes").Contains(_npc.Eyes)) { removed.Add(T("eyes")); _npc.Eyes = ""; }
            if (!string.IsNullOrEmpty(_npc.Hair) && _npc.Hair != "none" && !_catalog["hair"].Any(h => Id(h) == _npc.Hair))
            { removed.Add(T("hair")); _npc.Hair = "none"; _npc.HairColour = "prototype"; }
        }
        foreach (var id in LobbyWardrobeRules.RemoveIncompatible(_wardrobe, _npc.Clothing, Male(body)))
        {
            var item = Clothes.FirstOrDefault(g => Id(g) == id);
            removed.Add(item == null ? id : ClothingName(item));
        }
        Changed(); _message(removed.Count == 0 ? "" : string.Format(T("incompatibleRemoved"), string.Join(", ", removed))); BuildGrid();
    }
    private void BuildVariants()
    {
        _tiles.RemoveAll(t => _variants.Contains(t.button)); _variants.Clear();
        if (_tab == "hair")
        {
            var hair = _npc.Hair;
            _variants.Add(new Label(HairName(hair)));
            var variants = new ScrollView(); variants.AddToClassList("lobby-variant-scroll"); variants.contentContainer.AddToClassList("lobby-tile-grid"); _variants.Add(variants);
            var record = _catalog["hair"].FirstOrDefault(h => Id(h) == hair);
            var colours = record?["metadata"]?["colours"]?.Select(Id) ?? Enumerable.Empty<string>();
            var index = 0;
            foreach (var colour in new[] { "prototype" }.Concat(colours))
            {
                var title = colour == "prototype" ? T("prototype") : string.Format(T("colourNumber"), ++index);
                Tile(variants.contentContainer, title, () => _npc.HairColour == colour,
                    () => { _npc.HairColour = colour; Changed(); }, Look(colour: colour), true);
            }
        }
        else if (Array.IndexOf(Tabs, _tab) >= 4 && _selections.TryGetValue(_tab, out var model))
        {
            var items = Clothes.Where(Compatible).Where(g => Model(g) == model).ToArray();
            if (items.Length == 0) return;
            _variants.Add(new Label(ClothingName(items[0])));
            var variants = new ScrollView(); variants.AddToClassList("lobby-variant-scroll"); variants.contentContainer.AddToClassList("lobby-tile-grid"); _variants.Add(variants);
            foreach (var item in items)
            {
                var id = Id(item);
                Tile(variants.contentContainer, ClothingName(item), () => _npc.Clothing.Contains(id), () => Equip(item), iconId: id);
            }
            AddButton(_variants, T("unequip"), () => { _npc.Clothing.RemoveAll(id => items.Any(g => Id(g) == id)); Changed(); });
        }
        else _variants.Add(new Label(T("chooseVariant")));
        Highlight();
    }
    private void Equip(JToken item)
    {
        var selected = _wardrobe.First(g => g.Id == Id(item));
        if (!LobbyWardrobeRules.Equip(_wardrobe, _npc.Clothing, selected, out var conflict))
        {
            _message(T("validation.slotConflict") + ": " + ClothingName(Clothes.First(g => Id(g) == conflict))); return;
        }
        _message(""); Changed();
    }
    private void BuildParameters()
    {
        _parameters.Clear();
        foreach (var kind in Values("attributes")) Stat(_npc.Attributes, kind, "attr.");
        foreach (var kind in Values("skills")) Stat(_npc.Skills, kind, "skill.");
        foreach (var kind in Values("traits"))
        {
            var toggle = new Toggle(Loc.Get("trait." + kind.ToLowerInvariant() + ".title")) { value = _npc.Traits.Contains(kind) }; _parameters.Add(toggle);
            toggle.RegisterValueChangedCallback(e => { _npc.Traits.Remove(kind); if (e.newValue) _npc.Traits.Add(kind); _save(); });
        }
        var voices = Values("voices").ToList();
        var voice = new DropdownField(T("voice"), voices, Math.Max(0, voices.IndexOf(_npc.Voice))); _parameters.Add(voice);
        voice.RegisterValueChangedCallback(e => { _npc.Voice = e.newValue; _save(); });
    }
    private void Stat(List<CreationValue> values, string id, string prefix)
    {
        var value = values.FirstOrDefault(v => v.Id == id);
        if (value == null) { value = new CreationValue { Id = id }; values.Add(value); }
        var slider = new Slider(Loc.Get(prefix + id.ToLowerInvariant()), 0, 10) { value = value.Value * 10, showInputField = true }; _parameters.Add(slider);
        slider.RegisterValueChangedCallback(e => { value.Value = e.newValue / 10; _save(); });
    }
    public void Dispose() { if (_disposed) return; _disposed = true; _thumbnails.Dispose(); _preview.Dispose(); }
}
}
