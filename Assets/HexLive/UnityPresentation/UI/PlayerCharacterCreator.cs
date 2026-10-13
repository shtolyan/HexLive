using System;
using System.Collections.Generic;
using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Bootstrap;
using HexLive.UnityPresentation.Localization;
using HexLive.UnityPresentation.Wearing;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace HexLive.UnityPresentation.UI
{
// §171.20: the actual wardrobe/actor pipeline is also the character creation preview.
public sealed class PlayerCharacterCreator : IDisposable
{
    public readonly VisualElement Root;
    public readonly PlayerCreatorAppearance Appearance;
    private readonly LobbyCharacterPreview _preview;
    private readonly LobbyAppearanceThumbnails _thumbnails;
    private readonly ScrollView _controls;
    private readonly Label _status, _summary;
    private readonly Button _continue, _retry, _back;
    private readonly Action _changed, _submit;
    private readonly List<(Button button, Func<bool> selected)> _tiles = new();
    private readonly Dictionary<string, Button> _steps = new();
    private readonly Dictionary<string, Button> _eyeButtons = new();
    private string _tab = "appearance";
    private string _appearanceTab = "skin";
    private bool _disposed, _busy, _pending, _focusHead;
    private static string T(string key) => Loc.Get("creator." + key);
    public bool PreviewReady => _preview.IsReady;
    public PlayerCharacterCreator(VisualElement parent, MonoBehaviour runner, PlayerCreatorAppearance appearance,
        Action changed, Action submit, Action back, Action retry)
    {
        Appearance = appearance; _changed = changed; _submit = submit;
        Root = Resources.Load<VisualTreeAsset>("HexLive/UI/PlayerCharacterCreator").CloneTree();
        Root.AddToClassList("creator-overlay"); parent.Add(Root);
        _controls = Root.Q<ScrollView>("controls"); _status = Root.Q<Label>("status"); _summary = Root.Q<Label>("summary");
        Root.Q<Label>("eyebrow").text = T("eyebrow"); Root.Q<Label>("title").text = T("title");
        Root.Q<Label>("orbitHint").text = T("orbitHint");
        _back = Root.Q<Button>("back"); _back.text = T("back"); _back.clicked += back;
        _continue = Root.Q<Button>("continue"); _continue.name = "createCharacter";
        _continue.clicked += () => { if (_tab != "camp" && !_pending) SwitchTab(_tab == "appearance" ? "abilities" : "camp"); else _submit(); };
        _retry = Root.Q<Button>("retry"); _retry.text = T("retry"); _retry.clicked += retry;
        _preview = new LobbyCharacterPreview(Root.Q<VisualElement>("preview"), runner, Appearance.Character, creator: true);
        _thumbnails = new LobbyAppearanceThumbnails(runner);
        var cameras = Root.Q<VisualElement>("cameraControls");
        IconButton(cameras, "left", T("rotateLeft"), () => _preview.Rotate(-30));
        IconButton(cameras, "body", T("fullBody"), () => { _focusHead = false; _preview.FocusBody(); });
        IconButton(cameras, "face", T("face"), () => { _focusHead = true; });
        IconButton(cameras, "right", T("rotateRight"), () => _preview.Rotate(30));
        foreach (var tab in new[] { "appearance", "abilities", "camp" })
            _steps[tab] = AddButton(Root.Q<VisualElement>("steps"), T(tab), () => SwitchTab(tab), "creator-step");
        Root.RegisterCallback<GeometryChangedEvent>(e => Root.EnableInClassList("creator-compact", e.newRect.width < 820));
        Root.schedule.Execute(UpdateReady).Every(100);
        BuildControls(); UpdateReady();
    }
    private Button AddButton(VisualElement parent, string title, Action action, string css = null)
    {
        var button = new Button(action) { text = title };
        if (css != null) button.AddToClassList(css); parent.Add(button); return button;
    }
    private Button IconButton(VisualElement parent, string icon, string title, Action action)
    {
        var button = new Button(action) { tooltip = title }; button.AddToClassList("creator-icon-button");
        var image = new Image { vectorImage = Resources.Load<VectorImage>("HexLive/UI/CreatorIcons/" + icon), pickingMode = PickingMode.Ignore };
        image.AddToClassList("creator-icon"); button.Add(image);
        if (icon != "left" && icon != "right") button.Add(new Label(title) { pickingMode = PickingMode.Ignore });
        parent.Add(button); return button;
    }
    private void SwitchTab(string tab)
    {
        if (_busy || _pending) return;
        _tab = tab; BuildControls(); UpdateReady();
    }
    private void BuildControls()
    {
        _controls.Clear(); _tiles.Clear();
        BuildEyes();
        foreach (var pair in _steps) pair.Value.EnableInClassList("creator-selected", pair.Key == _tab);
        if (_tab == "appearance") BuildAppearance();
        else if (_tab == "abilities") BuildAbilities();
        else BuildCamps();
    }
    private void Heading(string title) { var label = new Label(title); label.AddToClassList("creator-section-title"); _controls.Add(label); }
    private void BuildEyes()
    {
        var panel=Root.Q("eyeControls");panel.Clear();_eyeButtons.Clear();
        panel.style.display=_tab=="appearance"?DisplayStyle.Flex:DisplayStyle.None;
        var title=new Label(T("eyes"));title.AddToClassList("creator-eye-title");panel.Add(title);
        var row=new VisualElement();row.AddToClassList("creator-eye-row");panel.Add(row);
        foreach(var id in Appearance.Eyes)
        {
            var label=T("eye."+(id.Length==0?"default":id));
            var button=AddButton(row,"",()=> {Appearance.Character.Eyes=id;_focusHead=true;Changed();},"creator-eye-button");
            button.name="creator-eye-"+(id.Length==0?"default":id);button.tooltip=label;
            var white=new VisualElement {pickingMode=PickingMode.Ignore};white.AddToClassList("creator-eye-white");
            var iris=new VisualElement {pickingMode=PickingMode.Ignore};iris.AddToClassList("creator-eye-iris");iris.style.backgroundColor=PeopleEyeColour.Swatch(id);
            var pupil=new VisualElement {pickingMode=PickingMode.Ignore};pupil.AddToClassList("creator-eye-pupil");
            iris.Add(pupil);white.Add(iris);button.Add(white);_eyeButtons[id]=button;
        }
    }
    private void Note(string text) { var label = new Label(text); label.AddToClassList("creator-note"); _controls.Add(label); }
    private VisualElement Row(string css)
    { var row = new VisualElement(); row.AddToClassList(css); _controls.Add(row); return row; }
    private void BuildAppearance()
    {
        var name = new TextField(T("name")) { value = Appearance.Character.Name, maxLength = 64, name = "creatorName" };
        _controls.Add(name); name.RegisterValueChangedCallback(e => { Appearance.Character.Name = e.newValue; Changed(false); });
        var sex = Row("creator-choice-row");
        foreach (var male in new[] { false, true })
        {
            var button = IconButton(sex, male ? "male" : "female", T(male ? "male" : "female"), () => { Appearance.SetSex(male); Changed(); BuildControls(); });
            button.name = male ? "creatorMale" : "creatorFemale";
            _tiles.Add((button, () => Appearance.IsMale == male));
            button.SetEnabled(Appearance.Bodies.Any(b => b.Value<string>("sex") == (male ? "male" : "female")));
        }
        var categories = Row("creator-choice-row");
        foreach (var category in new[] { "skin", "hair", "colour" })
        {
            var button = AddButton(categories, T(category), () => {
                _appearanceTab = category; _focusHead = category != "skin"; BuildControls();
            });
            button.EnableInClassList("creator-selected", category == _appearanceTab);
        }
        if (_appearanceTab == "skin") { BuildSkins(); Highlight(); return; }
        if (_appearanceTab == "hair") { BuildHair(); Highlight(); return; }
        BuildColours(); Highlight();
    }
    private void BuildSkins()
    {
        Heading(T("skin")); var skins = Row("creator-grid"); var index = 0;
        foreach (var skin in Appearance.Skins)
        {
            var title = Appearance.Label("skin", skin, index++);
            Tile(skins, title, () => Appearance.Character.Skin == skin,
                () => { Appearance.Character.Skin = skin; Changed(); }, Appearance.Look(skin: skin, hair: "none"), framing: 1);
        }
        Note(T("starterClothes"));
    }
    private void BuildHair()
    {
        var hair = Appearance.Hair.ToArray(); var selected = Array.IndexOf(hair, Appearance.Character.Hair);
        var selector = Row("creator-selector");
        IconButton(selector, "left", T("previousHair"), () => ChooseHair(hair, -1));
        selector.Add(new Label(Appearance.Label("hair", Appearance.Character.Hair, selected)));
        IconButton(selector, "right", T("nextHair"), () => ChooseHair(hair, 1));
        var styles = Row("creator-grid"); var index = 0;
        foreach (var id in hair)
            Tile(styles, Appearance.Label("hair", id, index++), () => Appearance.Character.Hair == id,
                () => SetHair(id), Appearance.Look(hair: id, colour: "prototype"));
    }
    private void BuildColours()
    {
        Heading(T("colour"));
        if (Appearance.Character.Hair != "none")
        {
            Note(Appearance.Label("hair", Appearance.Character.Hair));
            var colours = Row("creator-grid"); var index = 0;
            foreach (var colour in Appearance.Colours)
                Tile(colours, Appearance.Label("colour", colour, index++), () => Appearance.Character.HairColour == colour,
                    () => { Appearance.Character.HairColour = colour; Changed(); }, Appearance.Look(colour: colour), true);
        }
        else Note(T("chooseHairFirst"));
    }
    private void ChooseHair(string[] hair, int step)
    {
        if (hair.Length == 0) return;
        var index = Math.Max(0, Array.IndexOf(hair, Appearance.Character.Hair));
        SetHair(hair[(index + step + hair.Length) % hair.Length]);
    }
    private void SetHair(string hair)
    {
        if (Appearance.Character.Hair != hair)
        { Appearance.Character.Hair = hair; Appearance.Character.HairColour = Appearance.Colours.FirstOrDefault() ?? "prototype"; }
        Changed(); BuildControls();
    }
    private void Tile(VisualElement parent, string title, Func<bool> selected, Action choose, CharacterCreationConfig look, bool colour = false, int framing = 3)
    {
        var button = new Button(choose) { tooltip = title }; button.AddToClassList("creator-tile");
        if (colour) button.AddToClassList("creator-colour-tile");
        var image = new Image { scaleMode = ScaleMode.ScaleToFit, pickingMode = PickingMode.Ignore };
        image.AddToClassList("creator-tile-image"); button.Add(image);
        button.Add(new Label(title) { pickingMode = PickingMode.Ignore }); parent.Add(button); _tiles.Add((button, selected));
        image.schedule.Execute(() => {
            if (_disposed || image.panel == null || !_controls.contentViewport.worldBound.Overlaps(image.worldBound)) return;
            var texture = _thumbnails.GetOrRequest(look, framing); if (texture != null) image.image = texture;
        }).Every(200);
    }
    private void BuildAbilities()
    {
        Note(T("abilitiesHint"));
        Stats("attributes", Appearance.Character.Attributes, "attr.");
        Stats("skills", Appearance.Character.Skills, "skill.");
        Heading(T("traits"));
        foreach (var id in Appearance.Values("traits"))
        {
            var toggle = new Toggle(Loc.Get("trait." + id.ToLowerInvariant() + ".title")) { value = Appearance.Character.Traits.Contains(id) };
            _controls.Add(toggle);
            toggle.RegisterValueChangedCallback(e => {
                Appearance.Character.Traits.Remove(id);
                if (e.newValue)
                {
                    var opposite = id switch { "Slob" => "Neat", "Neat" => "Slob", "Lazy" => "Diligent", "Diligent" => "Lazy", "Coward" => "Brave", "Brave" => "Coward", _ => "" };
                    Appearance.Character.Traits.Remove(opposite); Appearance.Character.Traits.Add(id);
                }
                Changed(false); BuildControls();
            });
        }
    }
    private void Stats(string key, List<CreationValue> values, string prefix)
    {
        Heading(T(key)); var budget = new Label(); budget.AddToClassList("creator-budget"); _controls.Add(budget);
        var limit = Appearance.Options.Value<float>(key == "attributes" ? "attributeBudget" : "skillBudget");
        void Update() { budget.text = string.Format(T("budget"), (limit - values.Sum(v => v.Value)).ToString("0.00"), limit.ToString("0.00")); budget.EnableInClassList("creator-over-budget", !Appearance.ValidBudget(key)); }
        foreach (var value in values)
        {
            var slider = new Slider(Loc.Get(prefix + value.Id.ToLowerInvariant()), 0, 10) { value = value.Value * 10, showInputField = true };
            _controls.Add(slider); slider.RegisterValueChangedCallback(e => { value.Value = Mathf.Round(e.newValue * 10) / 100; Changed(false); Update(); });
        }
        Update();
    }
    private void BuildCamps()
    {
        Heading(T("camp")); Note(T("campHint"));
        foreach (var camp in Appearance.Options["camps"] ?? new JArray())
        {
            var id = camp.Value<string>("id"); var number = id == "Colony" ? "1" : id.Replace("Colony", "");
            var title = string.Format(T("campNumber"), number);
            var button = AddButton(_controls, title + "\n" + T(camp.Value<bool>("hasSpace") ? "campOpen" : "campFull"), () => {
                if (Enum.TryParse<Faction>(id, out var value)) { Appearance.Character.Camp = value; Changed(false); }
            });
            button.SetEnabled(camp.Value<bool>("hasSpace"));
            _tiles.Add((button, () => Appearance.Character.Camp.ToString() == id));
        }
        Note(T("confirmHint")); Highlight();
    }
    private void Highlight()
    {
        foreach (var tile in _tiles) tile.button.EnableInClassList("creator-selected", tile.selected());
        foreach(var pair in _eyeButtons) pair.Value.EnableInClassList("creator-selected",pair.Key==Appearance.Character.Eyes);
    }
    private void Changed(bool preview = true)
    { _changed(); if (preview) _preview.Refresh(Appearance.Character); Highlight(); UpdateReady(); }
    private void UpdateReady()
    {
        if (_disposed) return;
        if (_focusHead && _preview.IsReady) { _preview.FocusFace(); _focusHead = false; }
        _continue.text = T(_pending ? "retryCreation" : _tab == "camp" ? "create" : "next");
        _continue.SetEnabled(!_busy && (_pending || (_tab != "camp" ? !string.IsNullOrWhiteSpace(Appearance.Character.Name) : Appearance.CanSubmit && _preview.IsReady)));
        _summary.text = string.IsNullOrWhiteSpace(Appearance.Character.Name) ? T("summary") : Appearance.Character.Name.Trim();
        if (!_busy && !_pending && _preview.Failed) _status.text = T("error.appearance");
        _retry.EnableInClassList("creator-hidden", !_pending && !_preview.Failed);
    }
    public void SetState(bool busy, bool pending, string message)
    {
        _busy = busy; _pending = pending; _status.text = message;
        _controls.SetEnabled(!busy && !pending); _back.SetEnabled(!busy); _retry.SetEnabled(!busy);
        Root.Q("eyeControls").SetEnabled(!busy && !pending);
        UpdateReady();
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true; _thumbnails.Dispose(); _preview.Dispose(); Root.RemoveFromHierarchy();
    }
}
}
