using System;
using System.Collections.Generic;
using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Bootstrap;
using HexLive.UnityPresentation.Localization;
using HexLive.UnityPresentation.Platform;
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
    private readonly VisualElement _choices;
    private readonly Label _status, _summary;
    private readonly TextField _name;
    private readonly Label _nameError;
    private readonly Button _continue, _retry, _back;
    private readonly Action _changed, _submit;
    private readonly List<(Button button, Func<bool> selected)> _tiles = new();
    private readonly Dictionary<string, Button> _steps = new();
    private readonly Dictionary<string, Button> _eyeButtons = new();
    private string _tab = "appearance";
    private string _appearanceTab = "skin";
    private string _stateMessage = "";
    private bool _disposed, _busy, _pending, _focusHead;
    private static string T(string key) => Loc.Get("creator." + key);
    public bool PreviewReady => _preview.IsReady;

    // §174.7: in the web client the page draws this form (ui/screens/creator.js);
    // this class stays the controller and the UI Toolkit tree is hidden.
    public readonly bool Html;
    private readonly Action _backAction, _retryAction;
    private string _htmlLook = "skin";
    private bool _nameTouched;
    private float _nextThumbPoll;
    private static readonly string[][] OppositeTraits = { new[] { "Slob", "Neat" }, new[] { "Lazy", "Diligent" }, new[] { "Coward", "Brave" } };
    public PlayerCharacterCreator(VisualElement parent, MonoBehaviour runner, PlayerCreatorAppearance appearance,
        Action changed, Action submit, Action back, Action retry)
    {
        Appearance = appearance; _changed = changed; _submit = submit; _backAction = back; _retryAction = retry;
        Html = HtmlUi.Active;
        Root = Resources.Load<VisualTreeAsset>("HexLive/UI/PlayerCharacterCreator").CloneTree();
        Root.AddToClassList("creator-overlay"); parent.Add(Root);
        if (Html) Root.style.display = DisplayStyle.None;
        _controls = Root.Q<ScrollView>("controls"); _status = Root.Q<Label>("status"); _summary = Root.Q<Label>("summary");
        _choices = Root.Q<VisualElement>("choices");
        _name = Root.Q<TextField>("creatorName"); _name.label = T("name");
        _name.maxLength = 64; _name.isDelayed = false; _name.isReadOnly = false;
        _name.SetValueWithoutNotify(Appearance.Character.Name);
        Root.Q<Label>("nameHint").text = T("nameHint");
        _nameError = Root.Q<Label>("nameError");
        _name.RegisterValueChangedCallback(e => { Appearance.Character.Name = e.newValue; Changed(false); });
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
        Root.schedule.Execute(() => {
            if (!Html && !_disposed && !_busy && !_pending && string.IsNullOrWhiteSpace(Appearance.Character.Name)) _name.Focus();
        });
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
        _choices.Clear(); _tiles.Clear();
        BuildEyes();
        foreach (var pair in _steps) pair.Value.EnableInClassList("creator-selected", pair.Key == _tab);
        if (_tab == "appearance") BuildAppearance();
        else if (_tab == "abilities") BuildAbilities();
        else BuildCamps();
    }
    private void Heading(string title) { var label = new Label(title); label.AddToClassList("creator-section-title"); _choices.Add(label); }
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
    private void Note(string text) { var label = new Label(text); label.AddToClassList("creator-note"); _choices.Add(label); }
    private VisualElement Row(string css)
    { var row = new VisualElement(); row.AddToClassList(css); _choices.Add(row); return row; }
    private void BuildAppearance()
    {
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
            _choices.Add(toggle);
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
        Heading(T(key)); var budget = new Label(); budget.AddToClassList("creator-budget"); _choices.Add(budget);
        var limit = Appearance.Options.Value<float>(key == "attributes" ? "attributeBudget" : "skillBudget");
        void Update() { budget.text = string.Format(T("budget"), (limit - values.Sum(v => v.Value)).ToString("0.00"), limit.ToString("0.00")); budget.EnableInClassList("creator-over-budget", !Appearance.ValidBudget(key)); }
        foreach (var value in values)
        {
            var slider = new Slider(Loc.Get(prefix + value.Id.ToLowerInvariant()), 0, 10) { value = value.Value * 10, showInputField = true };
            _choices.Add(slider); slider.RegisterValueChangedCallback(e => { value.Value = Mathf.Round(e.newValue * 10) / 100; Changed(false); Update(); });
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
            var button = AddButton(_choices, title + "\n" + T(camp.Value<bool>("hasSpace") ? "campOpen" : "campFull"), () => {
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
    { if (!_busy && !_pending) _stateMessage = ""; _changed(); if (preview) _preview.Refresh(Appearance.Character); Highlight(); UpdateReady(); }
    private string BlockedReason()
    {
        if (_busy) return _stateMessage.Length > 0 ? _stateMessage : T("creating");
        if (_pending) return "";
        if (Appearance.NameErrorKey.Length > 0) return T(Appearance.NameErrorKey);
        if (_tab == "camp")
        {
            if (Appearance.ValidationErrorKey.Length > 0) return T(Appearance.ValidationErrorKey);
            if (!_preview.IsReady) return T(_preview.Failed ? "error.appearance" : "previewLoading");
        }
        return "";
    }
    private void UpdateReady()
    {
        if (_disposed) return;
        if (_focusHead && _preview.IsReady) { _preview.FocusFace(); _focusHead = false; }
        _continue.text = T(_pending ? "retryCreation" : _tab == "camp" ? "create" : "next");
        var reason = BlockedReason();
        _continue.SetEnabled(reason.Length == 0);
        _continue.tooltip = reason;
        var nameError = !_busy && !_pending ? Appearance.NameErrorKey : "";
        _nameError.text = nameError.Length > 0 ? T(nameError) : "";
        _nameError.EnableInClassList("creator-hidden", nameError.Length == 0);
        _summary.text = string.IsNullOrWhiteSpace(Appearance.Character.Name) ? T("summary") : Appearance.Character.Name.Trim();
        _status.text = reason.Length > 0 ? reason : _stateMessage.Length > 0 ? _stateMessage : _preview.Failed ? T("error.appearance") : "";
        _retry.EnableInClassList("creator-hidden", !_pending && !_preview.Failed);
    }
    public void SetState(bool busy, bool pending, string message)
    {
        _busy = busy; _pending = pending; _stateMessage = message ?? "";
        _controls.SetEnabled(!busy && !pending); _back.SetEnabled(!busy); _retry.SetEnabled(!busy);
        _name.SetEnabled(!busy && !pending);
        foreach (var step in _steps.Values) step.SetEnabled(!busy && !pending);
        Root.Q("eyeControls").SetEnabled(!busy && !pending);
        UpdateReady();
    }
    // ---- §174.7: the HTML form --------------------------------------------

    private string Blocked(string step)
    {
        if (_busy || _pending) return null;
        if (Appearance.NameErrorKey.Length > 0) return T(Appearance.NameErrorKey);
        if (step != "camp") return null;
        if (Appearance.ValidationErrorKey.Length > 0) return T(Appearance.ValidationErrorKey);
        return _preview.IsReady ? null : T(_preview.Failed ? "error.appearance" : "previewLoading");
    }

    private static int Points(float value) => Mathf.RoundToInt(value * 10);

    private static string Hex(Color colour) => "#" + ColorUtility.ToHtmlStringRGB(colour);

    private static string ThumbKey(CharacterCreationConfig look, int framing) =>
        string.Join("|", look.Body, look.Skin, look.Eyes, look.Hair, look.HairColour, framing);

    private IEnumerable<(string id, CharacterCreationConfig look, int framing)> Thumbs(string tab)
    {
        if (tab == "skin")
            foreach (var skin in Appearance.Skins) yield return ("skin:" + skin, Appearance.Look(skin: skin, hair: "none"), 1);
        else if (tab == "hair")
            foreach (var hair in Appearance.Hair) yield return ("hair:" + hair, Appearance.Look(hair: hair, colour: "prototype"), 3);
        else if (Appearance.Character.Hair != "none")
            // Palette tints are flat swatches; only authored colours need a picture.
            foreach (var colour in Appearance.Colours)
                if (colour != "prototype" && !colour.StartsWith("tint.", StringComparison.Ordinal))
                    yield return ("colour:" + colour, Appearance.Look(colour: colour), 3);
    }

    /// <summary>Once per frame from the loading screen: deliver finished thumbnails.</summary>
    public void HtmlTick()
    {
        if (!Html || _disposed || Time.realtimeSinceStartup < _nextThumbPoll) return;
        // The renderer forgets a request not asked for again within a second.
        _nextThumbPoll = Time.realtimeSinceStartup + .2f;
        foreach (var (_, look, framing) in Thumbs(_htmlLook))
        {
            var key = ThumbKey(look, framing);
            if (HtmlUi.HasAsset(key)) continue;
            var texture = _thumbnails.GetOrRequest(look, framing);
            if (texture != null) HtmlUi.Asset(key, "data:image/jpeg;base64," + Convert.ToBase64String(texture.EncodeToJPG(85)));
        }
    }

    public JObject HtmlModel(string bannerText)
    {
        var c = Appearance.Character;
        var labels = new JObject();
        var thumbs = new JObject();
        var tints = new JObject { ["prototype"] = "conic-gradient(#5a3a26, #b0763f, #3b2618, #5a3a26)" };
        var index = 0;
        foreach (var skin in Appearance.Skins) labels["skin:" + skin] = Appearance.Label("skin", skin, index++);
        index = 0;
        foreach (var hair in Appearance.Hair) labels["hair:" + hair] = Appearance.Label("hair", hair, index++);
        var hairOptions = new JArray();
        foreach (var hair in Appearance.Hair)
        {
            var colours = Appearance.Options["hair"]?.FirstOrDefault(h => h.Value<string>("id") == hair)?["colours"]?.Values<string>().ToArray()
                ?? new[] { "prototype" };
            hairOptions.Add(new JObject { ["id"] = hair, ["colours"] = new JArray(colours) });
            index = 0;
            foreach (var colour in colours)
            {
                labels["colour:" + colour] = Appearance.Label("colour", colour, index++);
                if (HexLive.Simulation.Content.HairTintPalette.TryGet(colour, out var tone))
                    tints[colour] = Hex(new Color32(tone.R, tone.G, tone.B, 255));
            }
        }
        foreach (var tab in new[] { "skin", "hair", "colour" })
            foreach (var (id, look, framing) in Thumbs(tab))
                thumbs[id] = ThumbKey(look, framing);
        var eyes = new JObject();
        foreach (var id in Appearance.Eyes) eyes[id.Length == 0 ? "default" : id] = Hex(PeopleEyeColour.Swatch(id));
        var camps = new JArray();
        foreach (var camp in Appearance.Options["camps"] ?? new JArray())
        {
            var id = camp.Value<string>("id");
            var entry = new JObject
            {
                ["id"] = id,
                ["number"] = int.TryParse(id == "Colony" ? "1" : id.Replace("Colony", ""), out var n) ? n : 0,
                ["hasSpace"] = camp.Value<bool>("hasSpace"),
            };
            // §171.10: head-counts come from a server that sends them.
            if (camp["residents"] != null) entry["residents"] = camp.Value<int>("residents");
            if (camp["capacity"] != null) entry["capacity"] = camp.Value<int>("capacity");
            camps.Add(entry);
        }
        var attributes = new JObject(); foreach (var v in c.Attributes) attributes[v.Id] = Points(v.Value);
        var skills = new JObject(); foreach (var v in c.Skills) skills[v.Id] = Points(v.Value);
        var sexes = new JArray(new[] { "female", "male" }.Where(sex => Appearance.Bodies.Any(b => b.Value<string>("sex") == sex)));
        var nameError = _nameTouched && !_busy && !_pending ? Appearance.NameErrorKey : "";
        return new JObject
        {
            ["phase"] = "editing",
            ["bannerText"] = string.IsNullOrEmpty(bannerText) ? null : bannerText,
            ["busyText"] = _busy ? (_stateMessage.Length > 0 ? _stateMessage : T("creating")) : null,
            ["pending"] = _pending,
            ["draft"] = new JObject
            {
                ["name"] = c.Name ?? "", ["sex"] = Appearance.IsMale ? "male" : "female", ["skin"] = c.Skin,
                ["hair"] = c.Hair, ["hairColour"] = c.HairColour, ["eyes"] = c.Eyes ?? "", ["camp"] = c.Camp.ToString(),
                ["attributes"] = attributes, ["skills"] = skills, ["traits"] = new JArray(c.Traits),
            },
            ["options"] = new JObject
            {
                ["sexes"] = sexes, ["skins"] = new JArray(Appearance.Skins), ["hair"] = hairOptions,
                ["eyes"] = new JArray(Appearance.Eyes), ["attributes"] = new JArray(Appearance.Values("attributes")),
                ["skills"] = new JArray(Appearance.Values("skills")), ["traits"] = new JArray(Appearance.Values("traits")),
                ["pairs"] = JArray.FromObject(OppositeTraits),
                ["attributeBudget"] = Points(Appearance.Options.Value<float>("attributeBudget")),
                ["skillBudget"] = Points(Appearance.Options.Value<float>("skillBudget")),
                ["camps"] = camps,
            },
            ["labels"] = labels, ["thumbs"] = thumbs, ["tints"] = tints, ["eyeSwatches"] = eyes,
            ["preview"] = _preview.Failed ? "failed" : _preview.IsReady ? "ready" : "loading",
            ["nameErrorText"] = nameError.Length > 0 ? T(nameError) : null,
            ["blocked"] = new JObject { ["appearance"] = Blocked("appearance"), ["abilities"] = Blocked("abilities"), ["camp"] = Blocked("camp") },
        };
    }

    /// <summary>The page's commands — the same effects as the UI Toolkit controls.</summary>
    public void HtmlCommand(string command, JObject args)
    {
        if (_disposed) return;
        switch (command)
        {
            case "set":
                if (!_busy && !_pending) Set((string)args["field"], args["value"]);
                break;
            case "submit":
                _submit();
                break;
            case "look":
                _htmlLook = (string)args["tab"] ?? "skin";
                _focusHead = _htmlLook != "skin";
                break;
            case "camera":
                var action = (string)args["action"];
                if (action == "left" || action == "right") _preview.Rotate(action == "left" ? -30 : 30);
                else if (action == "body") { _focusHead = false; _preview.FocusBody(); }
                else if (action == "face") _focusHead = true;
                else if (action == "orbit") _preview.OrbitBy((float?)args["dx"] ?? 0, (float?)args["dy"] ?? 0);
                else if (action == "zoom") _preview.Zoom((float?)args["dy"] ?? 0);
                break;
            case "previewRect":
                var vw = (float?)args["vw"] ?? 0; var vh = (float?)args["vh"] ?? 0;
                var w = (float?)args["w"] ?? 0; var h = (float?)args["h"] ?? 0;
                if (vw < 1 || vh < 1 || w < 1 || h < 1) break;
                var x = (float?)args["x"] ?? 0; var y = (float?)args["y"] ?? 0;
                // Page y grows down; Camera.rect is bottom-left.
                _preview.ShowOnScreen(new Rect(x / vw, 1 - (y + h) / vh, w / vw, h / vh));
                break;
            case "retry":
                _retryAction();
                break;
            case "back":
                _backAction();
                break;
        }
    }

    private void Set(string field, JToken value)
    {
        var c = Appearance.Character;
        switch (field)
        {
            case "name":
                _nameTouched = true;
                c.Name = (string)value ?? ""; _name.SetValueWithoutNotify(c.Name); Changed(false);
                break;
            case "sex":
                Appearance.SetSex((string)value == "male"); Changed();
                break;
            case "skin":
                if (Appearance.Skins.Contains((string)value)) { c.Skin = (string)value; Changed(); }
                break;
            case "hair":
                if (Appearance.Hair.Contains((string)value)) SetHair((string)value);
                break;
            case "hairColour":
                if (Appearance.Colours.Contains((string)value)) { c.HairColour = (string)value; Changed(); }
                break;
            case "eyes":
                if (Appearance.Eyes.Contains((string)value ?? "")) { c.Eyes = (string)value ?? ""; _focusHead = true; Changed(); }
                break;
            case "camp":
                if (Enum.TryParse<Faction>((string)value, out var camp)) { c.Camp = camp; Changed(false); }
                break;
            case "traits":
                var traits = value?.Values<string>().Where(id => Appearance.Values("traits").Contains(id)).Distinct().ToList() ?? new List<string>();
                foreach (var pair in OppositeTraits) // opposites exclude each other, as the toggles did
                    if (traits.Contains(pair[0]) && traits.Contains(pair[1])) traits.Remove(pair[0]);
                c.Traits.Clear(); c.Traits.AddRange(traits); Changed(false);
                break;
            case "attributes":
            case "skills":
                var values = field == "attributes" ? c.Attributes : c.Skills;
                if (value is JObject points)
                    foreach (var v in values)
                        if (points[v.Id] != null) v.Value = Mathf.Clamp((int)points[v.Id], 0, 10) / 10f;
                Changed(false);
                break;
        }
    }

    public void Dispose()
    {
        if (_disposed) return; _disposed = true; _thumbnails.Dispose(); _preview.Dispose(); Root.RemoveFromHierarchy();
    }
}
}
