using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Bootstrap;
using HexLive.UnityPresentation.Bootstrap;
using HexLive.UnityPresentation.Localization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace HexLive.UnityPresentation.UI
{
// §161: a pre-game editor; no local simulation and no world mutations until Start.
public sealed class ServerWorldLobby : IDisposable
{
    private readonly VisualElement _root;
    private readonly VisualElement _content;
    private readonly Label _status;
    private readonly MonoBehaviour _runner;
    private readonly Action<string, string> _connect;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private readonly string _previousServer = SessionConfig.ServerUrl;
    private readonly string _previousToken = SessionConfig.ControlToken;
    private string _server, _token, _playToken, _requestId, _submittedConfig;
    private JObject _capabilities, _preview;
    private WorldCreationConfig _config;
    private bool _closed, _busy, _connecting;
    private LobbyCharacterPreview _characterPreview;
    private static string T(string key) => Loc.Get("lobby." + key);

    public ServerWorldLobby(VisualElement parent, MonoBehaviour runner, Action<string, string> connect)
    {
        _runner = runner; _connect = connect;
        _root = Resources.Load<VisualTreeAsset>("HexLive/UI/ServerWorldLobby").CloneTree();
        _root.AddToClassList("lobby-overlay"); parent.Add(_root);
        _content = _root.Q<VisualElement>("content");
        _status = _root.Q<Label>("status");
        _root.Q<Label>("title").text = T("title");
        _root.Q<Button>("close").text = T("back");
        _root.Q<Button>("close").clicked += Dispose;
        Login();
    }
    private Button Button(VisualElement parent, string key, Action action)
    {
        var button = new Button(action) { text = T(key) }; parent.Add(button); return button;
    }
    private TextField Text(VisualElement parent, string key, string value, Action<string> changed = null, bool password = false)
    {
        var field = new TextField(T(key)) { value = value ?? string.Empty, isPasswordField = password };
        field.RegisterValueChangedCallback(e => { changed?.Invoke(e.newValue); SaveDraft(); }); parent.Add(field); return field;
    }
    private void Number(VisualElement parent, string key, int value, Action<int> changed)
    {
        var field = new IntegerField(T(key)) { value = value }; parent.Add(field);
        field.RegisterValueChangedCallback(e => { changed(e.newValue); SaveDraft(); });
    }
    private void Toggle(VisualElement parent, string key, bool value, Action<bool> changed)
    {
        var field = new Toggle(T(key)) { value = value }; parent.Add(field);
        field.RegisterValueChangedCallback(e => { changed(e.newValue); SaveDraft(); });
    }
    private void Choice(VisualElement parent, string label, IEnumerable<string> values, string selected, Action<string> changed, Func<string,string> format = null)
    {
        format ??= value => string.IsNullOrEmpty(value) ? T("authored") : value == "none" ? T("none") : value == "prototype" ? T("prototype") : value;
        var choices = values.Distinct().ToList();
        if (!choices.Contains(selected ?? string.Empty)) choices.Insert(0, selected ?? string.Empty);
        var field = new DropdownField(label, choices, Math.Max(0, choices.IndexOf(selected ?? string.Empty)), format, format);
        parent.Add(field); field.RegisterValueChangedCallback(e => { changed(e.newValue); SaveDraft(); });
    }
    private async void Run(Func<Task> action)
    {
        if (_busy || _closed) return;
        _busy = true; _content.SetEnabled(false); _status.text = T("working");
        try { await action(); if (!_closed && _status.text == T("working")) _status.text = string.Empty; }
        catch (Exception ex) { if (!_closed) _status.text = T("error") + "\n" + ex.Message; }
        finally { _busy = false; if (!_closed) _content.SetEnabled(true); }
    }
    private string FormatErrors(JArray errors) => string.Join("\n", errors.Select(error =>
    {
        var path = error.Value<string>("path") ?? "";
        var match = System.Text.RegularExpressions.Regex.Match(path, @"characters\[(\d+)\]");
        var prefix = match.Success && int.TryParse(match.Groups[1].Value, out var index) && _config != null && index < _config.Characters.Count
            ? _config.Characters[index].Name + ": " : "";
        return prefix + T("validation." + error.Value<string>("code"));
    }));
    private async Task<JToken> Api(string route, object body = null)
    {
        var origin = new UriBuilder(_server) { Scheme = _server.StartsWith("wss:", StringComparison.OrdinalIgnoreCase) ? "https" : "http", Path = "/api/worlds/v1" + route, Query = string.Empty };
        using var message = new HttpRequestMessage(body == null ? HttpMethod.Get : HttpMethod.Post, origin.Uri);
        if (!string.IsNullOrEmpty(_token)) message.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _token);
        if (body != null) message.Content = new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(message);
        var text = await response.Content.ReadAsStringAsync();
        var json = string.IsNullOrWhiteSpace(text) ? new JObject() : JToken.Parse(text);
        if (!response.IsSuccessStatusCode)
        {
            if (json["errors"] is JArray errors) throw new InvalidOperationException(FormatErrors(errors));
            throw new InvalidOperationException(T("http") + " " + (int)response.StatusCode);
        }
        return json;
    }
    private void Login()
    {
        _content.Clear();
        var server = Text(_content, "server", _previousServer);
        var password = Text(_content, "password", "", password: true);
        Button(_content, "login", () => Run(async () =>
        {
            _server = ServerBook.Normalize(server.value);
            if (string.IsNullOrEmpty(_server)) throw new InvalidOperationException(T("server"));
            var reply = await Api("/login", new { password = password.value }); password.value = "";
            _token = reply.Value<string>("token");
            if (reply.Value<bool>("controlEnabled") != true) throw new InvalidOperationException(T("controlDisabled"));
            _playToken = reply.Value<string>("playerToken");
            SessionConfig.UseServer(_server, _playToken);
            _capabilities = (JObject)await Api("/capabilities");
            var draft = DraftPath();
            if (File.Exists(draft))
            {
                var saved = JObject.Parse(File.ReadAllText(draft));
                _config = saved["config"]?.ToObject<WorldCreationConfig>();
                _preview = saved["preview"] as JObject;
                _requestId = saved.Value<string>("requestId");
                _submittedConfig = saved.Value<string>("submittedConfig");
            }
            if (_config != null && _preview != null) World(); else Setup();
        }));
    }
    private string DraftPath()
    {
        var id = Convert.ToBase64String(Encoding.UTF8.GetBytes(_server)).Replace('/', '_').Replace('+', '-').TrimEnd('=');
        return Path.Combine(Application.persistentDataPath, "world-drafts", id + ".json");
    }
    private void SaveDraft()
    {
        if (_config == null || string.IsNullOrEmpty(_server)) return;
        var current = JsonConvert.SerializeObject(_config);
        if (_requestId != null && _submittedConfig != current) { _requestId = null; _submittedConfig = null; }
        var path = DraftPath(); Directory.CreateDirectory(Path.GetDirectoryName(path));
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonConvert.SerializeObject(new { config = _config, preview = _preview, requestId = _requestId, submittedConfig = _submittedConfig }));
        if (File.Exists(path)) File.Replace(tmp, path, null);
        else File.Move(tmp, path);
    }
    private void Setup()
    {
        _content.Clear();
        var seed = new IntegerField(T("seed")) { value = _config?.Seed ?? UnityEngine.Random.Range(1, int.MaxValue) }; _content.Add(seed);
        var mode = _config?.Mode ?? GameMode.HugeIsland;
        Choice(_content, T("scenario"), Enum.GetNames(typeof(GameMode)), mode.ToString(), v => mode = Enum.Parse<GameMode>(v), v => Loc.Get("menu.newgame.mode." + v.ToLowerInvariant()));
        Button(_content, "generate", () => Run(async () =>
        {
            _preview = (JObject)await Api("/preview", new { seed = seed.value, mode = (int)mode, playerId = SessionConfig.ClientId });
            _config = _preview["config"].ToObject<WorldCreationConfig>(); _config.Name = T("defaultName");
            _requestId = null; SaveDraft(); World();
        }));
        Button(_content, "library", () => Run(Library));
    }
    private async Task Library()
    {
        var result = await Api(""); _content.Clear();
        foreach (var entry in result["worlds"])
        {
            var item = entry; var row = new VisualElement(); row.AddToClassList("lobby-card"); _content.Add(row);
            row.Add(new Label(item.Value<string>("name")));
            Button(row, "activate", () => Run(async () =>
            {
                await Api("/" + item.Value<string>("id") + "/activate", new {}); Connect();
            }));
        }
        Button(_content, "back", () => { if (_config != null) World(); else Setup(); });
    }
    private void World()
    {
        _characterPreview?.Dispose(); _characterPreview = null; _content.Clear();
        var settings = new VisualElement(); settings.AddToClassList("lobby-toolbar"); _content.Add(settings);
        settings.Add(new Label(Loc.Get("menu.newgame.mode." + _config.Mode.ToString().ToLowerInvariant()) + " · " + T("seed") + ": " + _config.Seed));
        Text(settings, "name", _config.Name, v => _config.Name = v);
        Number(settings, "population", _config.PopulationLimit, v => _config.PopulationLimit = v);
        if (_config.Mode == GameMode.Islands) Number(settings, "seaRaids", _config.SeaRaidIntervalDays, v => _config.SeaRaidIntervalDays = v);
        Button(settings, "regenerate", Setup); Button(settings, "library", () => Run(Library));
        _content.Add(new Label(T("mapLegend")));
        var columns = new VisualElement(); columns.AddToClassList("lobby-columns"); _content.Add(columns);
        var details = new ScrollView(); details.AddToClassList("lobby-details");
        var map = new LobbyWorldMap(_preview, _config, faction => Camp(details, faction));
        map.AddToClassList("lobby-map"); columns.Add(map); columns.Add(details);
        Camp(details, _config.PlayerCamp);
        var summary = new Label(); _content.Add(summary);
        void UpdateSummary()
        {
            var active = _config.Characters.Where(n => n.Enabled && _config.Camp(n.Camp)?.Enabled == true).ToArray();
            summary.text = string.Format(T("summary"), active.Length, active.Count(n => n.Controlled && n.Camp == _config.PlayerCamp), _config.Camps.Count(c => c.Enabled));
        }
        UpdateSummary(); summary.schedule.Execute(UpdateSummary).Every(300);
        Button(_content, "validate", () => Run(async () =>
        {
            var result = await Api("/validate", _config);
            if (result["errors"] is JArray errors && errors.Count > 0) throw new InvalidOperationException(FormatErrors(errors));
            _status.text = T("valid");
        }));
        Button(_content, "start", () => Run(async () =>
        {
            SaveDraft(); var checkedConfig = await Api("/validate", _config);
            var errors = (JArray)checkedConfig["errors"];
            if (errors.Count > 0) throw new InvalidOperationException(FormatErrors(errors));
            _requestId ??= Guid.NewGuid().ToString("N"); _submittedConfig = JsonConvert.SerializeObject(_config); SaveDraft();
            var operation = await Api("", new { requestId = _requestId, config = _config });
            while (!_closed && operation.Value<string>("state") == "running")
            {
                await Task.Delay(1000); operation = await Api("/operations/" + _requestId);
            }
            if (_closed) return;
            if (operation.Value<string>("state") != "complete")
            { SaveDraft(); throw new InvalidOperationException(operation.Value<string>("error")); }
            SaveDraft();
            Connect();
        }));
    }
    private void Camp(VisualElement details, Faction faction)
    {
        details.Clear(); var camp = _config.Camp(faction);
        details.Add(new Label(CampName(faction)));
        Toggle(details, "enabled", camp.Enabled, v => camp.Enabled = v);
        if (faction != Faction.Outsiders)
            Button(details, "myCamp", () =>
            {
                _config.PlayerCamp = faction; camp.Enabled = true;
                foreach (var n in _config.Characters.Where(n => n.Camp != faction)) n.Controlled = false;
                SaveDraft(); Camp(details, faction);
            });
        Number(details, "campPopulation", camp.PopulationLimit, v => camp.PopulationLimit = v);
        Number(details, "arrivals", camp.ArrivalIntervalDays, v => camp.ArrivalIntervalDays = v);
        if (faction == _config.PlayerCamp) Toggle(details, "controlArrivals", camp.ControlArrivals, v => camp.ControlArrivals = v);
        foreach (var npc in _config.Characters.Where(c => c.Camp == faction).ToArray())
        {
            var row = new VisualElement(); row.AddToClassList("lobby-card"); details.Add(row);
            row.Add(new Label(Loc.NpcName(npc.Name)));
            Toggle(row, "spawn", npc.Enabled, v => npc.Enabled = v);
            if (faction == _config.PlayerCamp) Toggle(row, "controlled", npc.Controlled, v => npc.Controlled = v);
            Button(row, "edit", () => Character(npc));
            Button(row, "duplicate", () =>
            {
                var copy = JsonConvert.DeserializeObject<CharacterCreationConfig>(JsonConvert.SerializeObject(npc));
                copy.Id = NextId(); copy.ProfileId = string.Empty; _config.Characters.Add(copy); SaveDraft(); Camp(details, faction);
            });
            Button(row, "remove", () => { _config.Characters.Remove(npc); SaveDraft(); Camp(details, faction); });
        }
        Button(details, "add", () =>
        {
            var n = new CharacterCreationConfig { Id = NextId(), Camp = faction, Name = T("newCharacter"), Controlled = faction == _config.PlayerCamp };
            Randomize(n, initialize: true); _config.Characters.Add(n); SaveDraft(); Character(n);
        });
    }
    private static string CampName(Faction f) => f == Faction.Outsiders ? T("outsiders") : T("camp") + " " + (f == Faction.Colony ? 1 : (int)f);
    private int NextId() => Enumerable.Range(1, 999).First(i => !_config.Characters.Any(n => n.Id == i));
    private void Randomize(CharacterCreationConfig n, bool initialize = false)
    {
        string Pick(string[] values, string fallback = "") => values.Length == 0 ? fallback : values[UnityEngine.Random.Range(0, values.Length)];
        n.Body = Pick(Values("bodies").ToArray(), n.Body);
        var male = n.Body is "Kshishtof" or "Tonny";
        n.Skin = male ? n.Body : Pick(Values("skins").ToArray(), n.Body);
        n.Eyes = male ? "" : Pick(Values("eyes").ToArray());
        n.Hair = male ? "none" : Pick(new[] { "none" }.Concat(_capabilities["hair"].Select(h => h.Value<string>("id"))).ToArray());
        n.Voice = male ? "kshishtof" : Pick(Values("voices").ToArray());
        var hair = _capabilities["hair"].FirstOrDefault(h => h.Value<string>("id") == n.Hair);
        var colours = hair?["metadata"]?["colours"]?.Select(c => c.Value<string>("id")).ToArray() ?? Array.Empty<string>();
        n.HairColour = Pick(new[] { "prototype" }.Concat(colours).ToArray());
        var compatible = _capabilities["clothing"].Where(g => g.Value<string>("sex") == "Any" || g.Value<string>("sex") == (male ? "Male" : "Female")).Select(g => g.Value<string>("id")).ToHashSet();
        n.Clothing.RemoveAll(id => !compatible.Contains(id));
        if (!initialize) return;
        var initial = _preview?["config"]?["characters"]?.ToObject<List<CharacterCreationConfig>>()?.FirstOrDefault(c => (c.Body is "Kshishtof" or "Tonny") == male);
        if (initial != null) n.Clothing = initial.Clothing.Where(compatible.Contains).ToList();
        n.Attributes = AttributeSet.All.Select(k => new CreationValue { Id = k.ToString(), Value = UnityEngine.Random.Range(.2f, .8f) }).ToList();
        n.Skills = SkillSet.All.Select(k => new CreationValue { Id = k.ToString(), Value = 0 }).ToList();
    }
    private IEnumerable<string> Values(string key) => _capabilities[key].Values<string>();
    private void Character(CharacterCreationConfig n)
    {
        _characterPreview?.Dispose(); _content.Clear();
        var columns = new VisualElement(); columns.AddToClassList("lobby-columns"); _content.Add(columns);
        var previewPanel = new VisualElement(); previewPanel.AddToClassList("lobby-preview"); columns.Add(previewPanel);
        _characterPreview = new LobbyCharacterPreview(previewPanel, _runner, n);
        var fields = new ScrollView(); fields.AddToClassList("lobby-details"); columns.Add(fields);
        Text(fields, "characterName", Loc.NpcName(n.Name), v => n.Name = v);
        Choice(fields, T("camp"), _config.Camps.Select(c => c.Faction.ToString()), n.Camp.ToString(), v => { n.Camp = Enum.Parse<Faction>(v); n.Controlled &= n.Camp == _config.PlayerCamp; }, v => CampName(Enum.Parse<Faction>(v)));
        Choice(fields, T("body"), Values("bodies"), n.Body, v => { n.Body = v; n.Skin = v; n.Eyes = v is "Kshishtof" or "Tonny" ? "" : "blue"; n.Hair = "none"; n.HairColour = "prototype"; n.Clothing.Clear(); Character(n); }, v => T("body." + v.ToLowerInvariant()));
        var male = n.Body is "Kshishtof" or "Tonny";
        Choice(fields, T("skin"), male ? new[] { n.Body } : Values("skins"), n.Skin, v => { n.Skin = v; RefreshPreview(n); });
        Choice(fields, T("eyes"), male ? new[] { "" } : Values("eyes"), n.Eyes, v => { n.Eyes = v; RefreshPreview(n); }, v => string.IsNullOrEmpty(v) ? T("authored") : T("eye." + v));
        Choice(fields, T("hair"), male ? new[] { "", "none" } : new[] { "none" }.Concat(_capabilities["hair"].Select(h => h.Value<string>("id"))), n.Hair, v => { n.Hair = v; n.HairColour = "prototype"; Character(n); });
        var record = _capabilities["hair"].FirstOrDefault(h => h.Value<string>("id") == n.Hair);
        var colours = record?["metadata"]?["colours"]?.Select(c => c.Value<string>("id")) ?? Enumerable.Empty<string>();
        Choice(fields, T("hairColour"), new[] { "prototype" }.Concat(colours), n.HairColour, v => { n.HairColour = v; RefreshPreview(n); });
        Choice(fields, T("voice"), Values("voices"), n.Voice, v => n.Voice = v);
        foreach (var kind in Values("attributes")) Value(fields, n.Attributes, kind, "attr.");
        foreach (var kind in Values("skills")) Value(fields, n.Skills, kind, "skill.");
        foreach (var kind in Values("traits"))
        {
            var toggle = new Toggle(Loc.Get("trait." + kind.ToLowerInvariant() + ".title")) { value = n.Traits.Contains(kind) }; fields.Add(toggle);
            toggle.RegisterValueChangedCallback(e => { n.Traits.Remove(kind); if (e.newValue) n.Traits.Add(kind); SaveDraft(); });
        }
        var clothing = new Foldout { text = T("clothing"), value = true }; fields.Add(clothing);
        var search = Text(clothing, "search", "");
        var list = new ScrollView(); list.AddToClassList("lobby-clothing"); clothing.Add(list);
        string ClothingName(JToken item)
        {
            var id = item.Value<string>("id");
            var key = HexLive.Simulation.Content.ItemCatalog.Resolve(id).NameKey;
            return Loc.Has(key) ? Loc.Get(key) : item.Value<string>("name") ?? id;
        }
        var layer = "all";
        var slot = "all";
        var page = 0;
        Choice(clothing, T("layer"), new[] { "all" }.Concat(_capabilities["clothing"].Select(g => g.Value<string>("layer"))), layer,
            v => { layer = v; page = 0; Clothes(); }, v => T("layer." + v.ToLowerInvariant()));
        Choice(clothing, T("slot"), new[] { "all" }.Concat(_capabilities["clothing"].SelectMany(g => g["slots"].Values<string>())), slot,
            v => { slot = v; page = 0; Clothes(); }, v => T("slot." + v.ToLowerInvariant()));
        Button(clothing, "previous", () => { page = Math.Max(0, page - 1); Clothes(); });
        Button(clothing, "next", () => { page++; Clothes(); });
        void Clothes()
        {
            list.Clear();
            var available = _capabilities["clothing"].Where(g =>
                (g.Value<string>("sex") == "Any" || g.Value<string>("sex") == (male ? "Male" : "Female")) &&
                (layer == "all" || g.Value<string>("layer") == layer) &&
                (slot == "all" || g["slots"].Values<string>().Contains(slot)) &&
                (string.IsNullOrEmpty(search.value) || ClothingName(g).IndexOf(search.value, StringComparison.OrdinalIgnoreCase) >= 0)).ToArray();
            page = Math.Min(page, Math.Max(0, (available.Length - 1) / 40));
            foreach (var item in available.Skip(page * 40).Take(40))
            {
                var id = item.Value<string>("id"); var name = ClothingName(item);
                if (!string.IsNullOrEmpty(search.value) && (name ?? id).IndexOf(search.value, StringComparison.OrdinalIgnoreCase) < 0) continue;
                var row = new VisualElement(); row.AddToClassList("lobby-clothing-row"); list.Add(row);
                var icon = new Image(); icon.AddToClassList("lobby-item-icon"); row.Add(icon);
                icon.schedule.Execute(() => icon.sprite = Wearing.Garments.ItemIcons.Load(id)).Every(300);
                var choice = new Toggle(name ?? id) { value = n.Clothing.Contains(id) }; row.Add(choice);
                choice.RegisterValueChangedCallback(e =>
                {
                    n.Clothing.Remove(id);
                    if (e.newValue)
                    {
                        var covers = item["covers"].Values<string>().ToHashSet();
                        var conflict = _capabilities["clothing"].Any(g => n.Clothing.Contains(g.Value<string>("id")) && g.Value<string>("layer") == item.Value<string>("layer") && (g.Value<bool>("authoredSlots") && item.Value<bool>("authoredSlots")
                            ? g["slots"].Values<string>().Intersect(item["slots"].Values<string>()).Any()
                            : g["covers"].Values<string>().Any(covers.Contains)));
                        if (conflict) { choice.SetValueWithoutNotify(false); _status.text = T("validation.slotConflict"); return; }
                        n.Clothing.Add(id);
                    }
                    SaveDraft(); RefreshPreview(n);
                });
            }
        }
        search.RegisterValueChangedCallback(_ => { page = 0; Clothes(); }); Clothes();
        Button(fields, "random", () => { Randomize(n); SaveDraft(); Character(n); });
        Button(_content, "done", () => { SaveDraft(); World(); });
    }
    private void Value(VisualElement fields, List<CreationValue> values, string id, string prefix)
    {
        var value = values.FirstOrDefault(v => v.Id == id);
        if (value == null) { value = new CreationValue { Id = id }; values.Add(value); }
        var field = new Slider(Loc.Get(prefix + id.ToLowerInvariant()), 0, 10) { value = value.Value * 10, showInputField = true }; fields.Add(field);
        field.RegisterValueChangedCallback(e => { value.Value = e.newValue / 10; SaveDraft(); });
    }
    private void RefreshPreview(CharacterCreationConfig n) => _characterPreview?.Refresh(n);
    private void Connect()
    {
        _connecting = true; Dispose(); _connect(_server, _playToken);
    }
    public void Dispose()
    {
        if (_closed) return;
        _closed = true; SaveDraft(); _characterPreview?.Dispose(); _http.Dispose(); _root.RemoveFromHierarchy();
        if (!_connecting) SessionConfig.UseServer(_previousServer, _previousToken);
    }
}
}
