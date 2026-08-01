#nullable enable
using System.Collections.Generic;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Debug;
using HexLive.UnityPresentation.Bootstrap;
using HexLive.UnityPresentation.Input;
using HexLive.UnityPresentation.Localization;
using UnityEngine;
using UnityEngine.UIElements;

namespace HexLive.UnityPresentation.UI
{
    [RequireComponent(typeof(UIDocument))]
    public sealed class HexInspectorPanel : MonoBehaviour
    {
        [SerializeField] private SimulationRunnerBehaviour? _runner;
        [SerializeField] private float _uiScale = 1f;

        private UIDocument _document = null!;
        private VisualElement _root = null!;
        private VisualElement _card = null!;
        private Label _title = null!;
        private Label _subtitle = null!;
        private ScrollView _scroll = null!;

        private int _refreshedTick = -1;
        private TileCoord? _refreshedCoord;

        public static bool PointerOverPanel { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            PointerOverPanel = false;
        }

        private static readonly Color Text = new(0.906f, 0.925f, 0.937f);
        private static readonly Color TextDim = new(0.604f, 0.651f, 0.678f);
        private static readonly Color TextMute = new(0.400f, 0.447f, 0.478f);
        private static readonly Color Panel = new(0.075f, 0.094f, 0.110f, 0.96f);
        private static readonly Color PanelMid = new(0.102f, 0.125f, 0.149f, 0.96f);
        private static readonly Color Raised = new(0.133f, 0.165f, 0.192f);
        private static readonly Color Stroke = new(1f, 1f, 1f, 0.10f);
        private static readonly Color StrokeStrong = new(1f, 1f, 1f, 0.14f);
        private static readonly Color Gold = new(0.941f, 0.706f, 0.361f);
        private static readonly Color Good = new(0.373f, 0.769f, 0.416f);
        private static readonly Color Warn = new(0.910f, 0.698f, 0.235f);
        private static readonly Color Crit = new(0.910f, 0.341f, 0.310f);
        private static readonly Color Water = new(0.278f, 0.714f, 0.902f);

        public void SetRunner(SimulationRunnerBehaviour runner) => _runner = runner;

        private void Awake()
        {
            PointerOverPanel = false;
            _document = GetComponent<UIDocument>();
            var reference = new Vector2Int(
                Mathf.RoundToInt(1920f / Mathf.Max(0.25f, _uiScale)),
                Mathf.RoundToInt(1080f / Mathf.Max(0.25f, _uiScale)));

            var baseSettings = Resources.Load<PanelSettings>("HexLive/DebugPanelSettings");
            if (baseSettings != null)
            {
                var settings = Instantiate(baseSettings);
                settings.name = "HexInspectorPanelSettings";
                settings.scaleMode = PanelScaleMode.ScaleWithScreenSize;
                settings.screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight;
                settings.referenceResolution = reference;
                settings.match = 1f;
                settings.sortingOrder = 155;
                _document.panelSettings = settings;
            }

            BuildUi();
            _root.style.display = DisplayStyle.None;
        }

        private void OnEnable()
        {
            HexSelection.SelectionChanged += OnSelectionChanged;
            HexSelection.EnabledChanged += OnEnabledChanged;
            Loc.LanguageChanged += Invalidate;
        }

        private void OnDisable()
        {
            HexSelection.SelectionChanged -= OnSelectionChanged;
            HexSelection.EnabledChanged -= OnEnabledChanged;
            Loc.LanguageChanged -= Invalidate;
            PointerOverPanel = false;
        }

        private void Update()
        {
            if (_runner == null)
            {
                _runner = FindAnyObjectByType<SimulationRunnerBehaviour>();
            }

            if (!HexSelection.HasSelection)
            {
                return;
            }

            Refresh();
        }

        private void OnEnabledChanged(bool enabled)
        {
            if (!enabled)
            {
                _root.style.display = DisplayStyle.None;
                PointerOverPanel = false;
            }
        }

        private void OnSelectionChanged(TileCoord? coord)
        {
            _refreshedTick = -1;
            _refreshedCoord = null;
            _root.style.display = HexSelection.Enabled && coord.HasValue ? DisplayStyle.Flex : DisplayStyle.None;
        }

        private void Invalidate()
        {
            _refreshedTick = -1;
            _refreshedCoord = null;
        }

        private void Refresh()
        {
            var snapshot = _runner != null && _runner.IsReady ? _runner.CreateSnapshot() : null;
            if (snapshot == null)
            {
                return;
            }

            var coord = HexSelection.SelectedCoord;
            if (_refreshedTick == snapshot.Tick && _refreshedCoord.HasValue && _refreshedCoord.Value == coord)
            {
                return;
            }

            _refreshedTick = snapshot.Tick;
            _refreshedCoord = coord;
            Rebuild(snapshot, coord);
        }

        private void Rebuild(WorldSnapshot snapshot, TileCoord coord)
        {
            _scroll.Clear();
            var tile = FindTile(snapshot, coord);

            _title.text = "Map point";
            _subtitle.text = $"Q {coord.Q} / R {coord.R}";

            if (tile == null)
            {
                _scroll.Add(MakeEmpty("No hex data in the current snapshot."));
                return;
            }

            var objects = FindObjects(snapshot, coord);
            if (objects.Count > 0)
            {
                _scroll.Add(MakeSectionTitle("Game info"));
                foreach (var obj in objects)
                {
                    _scroll.Add(MakeObjectCard(obj));
                }
            }
            else
            {
                _scroll.Add(MakeSectionTitle("Game info"));
                _scroll.Add(MakeEmpty("No items or objects on this point."));
            }

            var npcs = FindNpcs(snapshot, coord);
            if (npcs.Count > 0)
            {
                foreach (var npc in npcs)
                {
                    _scroll.Add(MakeNpcRow(npc));
                }
            }

            _scroll.Add(MakeSectionTitle("Hex data"));
            _scroll.Add(MakeStatGrid(new[]
            {
                ("Walkable", tile.Walkable ? Yes() : No(), tile.Walkable ? Good : Crit),
                ("Blocked", tile.Blocked ? Yes() : No(), tile.Blocked ? Crit : Good),
                ("Indoor", tile.Indoor ? Yes() : No(), tile.Indoor ? Warn : TextDim),
                ("Water", tile.Water ? Yes() : No(), tile.Water ? Water : TextDim),
                ("Elevation", tile.Elevation.ToString(), Text),
                ("Coord", $"{coord.Q},{coord.R}", TextDim)
            }));

            var junctions = FindJunctions(snapshot, coord);
            if (junctions.Count > 0)
            {
                _scroll.Add(MakeSectionTitle("Debug: junctions"));
                foreach (var j in junctions)
                {
                    _scroll.Add(MakeJunctionRow(j));
                }
            }
        }

        private VisualElement MakeObjectCard(ObjectSnapshot obj)
        {
            var def = ResolveDef(obj.DefinitionId);
            var info = ResolveItemInfo(obj.DefinitionId, def);
            var accent = CategoryColor(info.Category);

            var card = new VisualElement();
            card.style.backgroundColor = Raised;
            card.style.marginBottom = 8f;
            card.style.paddingLeft = 11f;
            card.style.paddingRight = 11f;
            card.style.paddingTop = 10f;
            card.style.paddingBottom = 10f;
            SetBorder(card, Stroke, 1f);
            SetRadius(card, 8f);

            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;

            var iconWrap = new VisualElement();
            iconWrap.style.width = 48f;
            iconWrap.style.height = 48f;
            iconWrap.style.flexShrink = 0f;
            iconWrap.style.marginRight = 11f;
            iconWrap.style.alignItems = Align.Center;
            iconWrap.style.justifyContent = Justify.Center;
            iconWrap.style.backgroundColor = PanelMid;
            SetBorder(iconWrap, new Color(accent.r, accent.g, accent.b, 0.35f), 1f);
            SetRadius(iconWrap, 8f);

            var sprite = LoadItemIcon(obj.DefinitionId);
            if (sprite != null)
            {
                var image = new Image { sprite = sprite, scaleMode = ScaleMode.ScaleToFit };
                image.style.width = 40f;
                image.style.height = 40f;
                iconWrap.Add(image);
            }
            else
            {
                var glyph = new Label(info.Emoji);
                glyph.style.fontSize = 25;
                glyph.style.unityTextAlign = TextAnchor.MiddleCenter;
                iconWrap.Add(glyph);
            }

            row.Add(iconWrap);

            var text = new VisualElement();
            text.style.flexGrow = 1f;
            var name = new Label(ItemName(def, info));
            name.style.color = Text;
            name.style.fontSize = 15;
            name.style.unityFontStyleAndWeight = FontStyle.Bold;
            name.style.whiteSpace = WhiteSpace.Normal;
            text.Add(name);

            var cat = new Label(ItemCategoryName(def, info).ToUpperInvariant());
            cat.style.color = accent;
            cat.style.fontSize = 10;
            cat.style.unityFontStyleAndWeight = FontStyle.Bold;
            cat.style.marginTop = 3f;
            text.Add(cat);
            row.Add(text);
            card.Add(row);

            var desc = new Label(ItemDesc(def, info));
            desc.style.color = TextDim;
            desc.style.fontSize = 12;
            desc.style.whiteSpace = WhiteSpace.Normal;
            desc.style.marginTop = 8f;
            card.Add(desc);

            card.Add(MakeInlineStats(obj, def));
            return card;
        }

        private VisualElement MakeInlineStats(ObjectSnapshot obj, ObjectDefinition? def)
        {
            var box = new VisualElement();
            box.style.marginTop = 9f;

            box.Add(MakeStatRow("ID", $"#{obj.Id.Value} / {obj.DefinitionId}", TextDim));

            if (!string.IsNullOrEmpty(obj.BuildProduct))
            {
                box.Add(MakeStatRow(
                    "Build site",
                    $"{obj.BuildProduct} / {Delivered(obj)}/{Bill(obj)}",
                    Gold));
                AddBillLine(box, "Logs", obj.DeliveredLogs, obj.BillLogs);
                AddBillLine(box, "Stones", obj.DeliveredStones, obj.BillStones);
                AddBillLine(box, "Leaves", obj.DeliveredLeaves, obj.BillLeaves);
                AddBillLine(box, "Sticks", obj.DeliveredSticks, obj.BillSticks);
                AddBillLine(box, "Rope", obj.DeliveredRope, obj.BillRope);
            }

            if (obj.ResourceAmount > 0.001f)
            {
                box.Add(MakeStatRow(
                    "Resource",
                    obj.ResourceAmount.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture),
                    Warn));
            }

            if (obj.OwnerNpcId.HasValue)
            {
                box.Add(MakeStatRow("Owner NPC", $"#{obj.OwnerNpcId.Value}", TextDim));
            }

            if (!string.IsNullOrEmpty(obj.Variant))
            {
                box.Add(MakeStatRow("Variant", obj.Variant, TextDim));
            }

            if (def != null && def.Tags.Count > 0)
            {
                box.Add(MakeStatRow("Tags", string.Join(", ", def.Tags), TextDim));
            }

            return box;
        }

        private void BuildUi()
        {
            _root = _document.rootVisualElement;
            _root.style.position = Position.Absolute;
            _root.style.left = 0f;
            _root.style.right = 0f;
            _root.style.top = 0f;
            _root.style.bottom = 0f;
            _root.pickingMode = PickingMode.Ignore;

            _card = new VisualElement();
            _card.style.position = Position.Absolute;
            _card.style.top = 20f;
            _card.style.right = 20f;
            _card.style.width = 390f;
            _card.style.maxHeight = 620f;
            _card.style.backgroundColor = Panel;
            _card.style.paddingLeft = 14f;
            _card.style.paddingRight = 14f;
            _card.style.paddingTop = 12f;
            _card.style.paddingBottom = 14f;
            _card.pickingMode = PickingMode.Position;
            SetBorder(_card, StrokeStrong, 1f);
            SetRadius(_card, 10f);

            _card.RegisterCallback<PointerEnterEvent>(_ =>
            {
                PointerOverPanel = true;
                NpcSelection.PointerOverUi = true;
            });
            _card.RegisterCallback<PointerLeaveEvent>(_ =>
            {
                PointerOverPanel = false;
                NpcSelection.PointerOverUi = false;
            });

            var header = new VisualElement();
            header.style.flexDirection = FlexDirection.Row;
            header.style.alignItems = Align.Center;
            header.style.marginBottom = 10f;

            var text = new VisualElement();
            text.style.flexGrow = 1f;
            _title = new Label();
            _title.style.color = Text;
            _title.style.fontSize = 17;
            _title.style.unityFontStyleAndWeight = FontStyle.Bold;
            text.Add(_title);

            _subtitle = new Label();
            _subtitle.style.color = TextMute;
            _subtitle.style.fontSize = 11;
            _subtitle.style.marginTop = 2f;
            text.Add(_subtitle);
            header.Add(text);

            var close = new Label("x");
            close.style.width = 24f;
            close.style.height = 24f;
            close.style.flexShrink = 0f;
            close.style.color = TextDim;
            close.style.fontSize = 15;
            close.style.unityTextAlign = TextAnchor.MiddleCenter;
            close.style.unityFontStyleAndWeight = FontStyle.Bold;
            SetRadius(close, 6f);
            close.RegisterCallback<MouseEnterEvent>(_ => close.style.color = Text);
            close.RegisterCallback<MouseLeaveEvent>(_ => close.style.color = TextDim);
            close.RegisterCallback<MouseDownEvent>(evt =>
            {
                HexSelection.Clear();
                evt.StopPropagation();
            });
            header.Add(close);
            _card.Add(header);

            _scroll = new ScrollView(ScrollViewMode.Vertical);
            _scroll.style.maxHeight = 560f;
            _card.Add(_scroll);
            _root.Add(_card);
        }

        private ObjectDefinition? ResolveDef(string id)
        {
            // Static content — see CharacterPanel.ResolveDef.
            return _runner != null && _runner.TryGetObjectDefinition(id, out var def) ? def : null;
        }

        private static TileSnapshot? FindTile(WorldSnapshot snapshot, TileCoord coord)
        {
            foreach (var tile in snapshot.Tiles)
            {
                if (tile.Coord == coord) return tile;
            }
            return null;
        }

        private static List<ObjectSnapshot> FindObjects(WorldSnapshot snapshot, TileCoord coord)
        {
            var list = new List<ObjectSnapshot>();
            foreach (var obj in snapshot.Objects)
            {
                if (obj.Tile == coord) list.Add(obj);
            }
            return list;
        }

        private static List<NpcSnapshot> FindNpcs(WorldSnapshot snapshot, TileCoord coord)
        {
            var list = new List<NpcSnapshot>();
            foreach (var npc in snapshot.Npcs)
            {
                if (npc.Tile == coord) list.Add(npc);
            }
            return list;
        }

        private static List<JunctionSnapshot> FindJunctions(WorldSnapshot snapshot, TileCoord coord)
        {
            var list = new List<JunctionSnapshot>();
            foreach (var junction in snapshot.Junctions)
            {
                foreach (var tile in junction.Tiles)
                {
                    if (tile == coord)
                    {
                        list.Add(junction);
                        break;
                    }
                }
            }
            return list;
        }

        private VisualElement MakeNpcRow(NpcSnapshot npc)
        {
            return MakeStatRow(
                "Character",
                // §74: DisplayName is a name ID; the player sees the localized term.
                string.IsNullOrEmpty(npc.DisplayName)
                    ? $"NPC #{npc.Id.Value}"
                    : $"{Loc.NpcName(npc.DisplayName)} / #{npc.Id.Value}",
                Good);
        }

        private VisualElement MakeJunctionRow(JunctionSnapshot j)
        {
            var value = $"{j.Id.Value} / " +
                $"blocked:{Bool(j.Blocked)} occupied:{Bool(j.Occupied)} reserved:{Bool(j.Reserved)}";
            if (j.IsClimbSeam) value += " / climb";
            if (j.IsSwimmable) value += " / swim";
            return MakeStatRow("Junction", value, TextDim);
        }

        private VisualElement MakeStatGrid((string label, string value, Color color)[] rows)
        {
            var box = new VisualElement();
            box.style.backgroundColor = PanelMid;
            box.style.paddingLeft = 10f;
            box.style.paddingRight = 10f;
            box.style.paddingTop = 8f;
            box.style.paddingBottom = 8f;
            box.style.marginBottom = 8f;
            SetBorder(box, Stroke, 1f);
            SetRadius(box, 8f);
            foreach (var row in rows)
            {
                box.Add(MakeStatRow(row.label, row.value, row.color));
            }
            return box;
        }

        private static VisualElement MakeStatRow(string label, string value, Color valueColor)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.FlexStart;
            row.style.marginTop = 2f;
            row.style.marginBottom = 2f;

            var left = new Label(label);
            left.style.color = TextMute;
            left.style.fontSize = 11;
            left.style.width = 92f;
            left.style.flexShrink = 0f;
            row.Add(left);

            var right = new Label(value);
            right.style.color = valueColor;
            right.style.fontSize = 11;
            right.style.whiteSpace = WhiteSpace.Normal;
            right.style.flexGrow = 1f;
            row.Add(right);
            return row;
        }

        private static Label MakeSectionTitle(string text)
        {
            var label = new Label(text.ToUpperInvariant());
            label.style.color = Gold;
            label.style.fontSize = 10;
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            label.style.marginTop = 5f;
            label.style.marginBottom = 7f;
            return label;
        }

        private static Label MakeEmpty(string text)
        {
            var label = new Label(text);
            label.style.color = TextDim;
            label.style.fontSize = 12;
            label.style.whiteSpace = WhiteSpace.Normal;
            label.style.backgroundColor = PanelMid;
            label.style.paddingLeft = 10f;
            label.style.paddingRight = 10f;
            label.style.paddingTop = 8f;
            label.style.paddingBottom = 8f;
            label.style.marginBottom = 8f;
            SetBorder(label, Stroke, 1f);
            SetRadius(label, 8f);
            return label;
        }

        private static void AddBillLine(VisualElement box, string label, int delivered, int bill)
        {
            if (bill <= 0) return;
            box.Add(MakeStatRow(label, $"{delivered}/{bill}", delivered >= bill ? Good : Warn));
        }

        private static int Delivered(ObjectSnapshot obj) =>
            obj.DeliveredLogs + obj.DeliveredStones + obj.DeliveredLeaves + obj.DeliveredSticks + obj.DeliveredRope;

        private static int Bill(ObjectSnapshot obj) =>
            obj.BillLogs + obj.BillStones + obj.BillLeaves + obj.BillSticks + obj.BillRope;

        private static string Yes() => "yes";
        private static string No() => "no";
        private static string Bool(bool value) => value ? "1" : "0";

        private static ItemInfo ResolveItemInfo(string id, ObjectDefinition? def)
        {
            if (def != null) return ItemCatalog.Resolve(def);
            var info = ItemCatalog.Resolve(id);
            return Loc.Has(info.NameKey)
                ? info
                : new ItemInfo(id, ItemCategory.Misc, ItemCatalog.CategoryEmoji(ItemCategory.Misc));
        }

        private static string ItemName(ObjectDefinition? def, ItemInfo info)
        {
            if (Loc.Has(info.NameKey)) return Loc.Get(info.NameKey);
            if (def != null && !string.IsNullOrEmpty(def.DisplayName)) return def.DisplayName;
            return string.IsNullOrEmpty(info.DefinitionId) ? "?" : info.DefinitionId;
        }

        private static string ItemCategoryName(ObjectDefinition? def, ItemInfo info) =>
            def == null && !Loc.Has(info.NameKey)
                ? "Object"
                : Loc.Get(info.CategoryNameKey);

        private static string ItemDesc(ObjectDefinition? def, ItemInfo info)
        {
            if (Loc.Has(info.DescKey)) return Loc.Get(info.DescKey);
            if (def != null) return Loc.Get(info.CategoryDescKey);
            return "Data is available from the object model.";
        }

        private static Sprite? LoadItemIcon(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            var slug = ItemInfo.Slug(id);
            return Resources.Load<Sprite>($"HexLive/UI/Items/{id}") ??
                Resources.Load<Sprite>($"HexLive/UI/Items/{slug}");
        }

        private static Color CategoryColor(ItemCategory category) => category switch
        {
            ItemCategory.Weapon => new Color(0.910f, 0.451f, 0.408f),
            ItemCategory.Tool => new Color(0.639f, 0.682f, 0.729f),
            ItemCategory.Clothing => new Color(0.490f, 0.580f, 0.918f),
            ItemCategory.Armor => new Color(0.710f, 0.545f, 0.886f),
            ItemCategory.Food => new Color(0.910f, 0.569f, 0.235f),
            ItemCategory.Water => Water,
            ItemCategory.Medicine => Good,
            ItemCategory.Resource => new Color(0.780f, 0.690f, 0.529f),
            _ => TextMute
        };

        private static void SetRadius(VisualElement e, float radius)
        {
            e.style.borderTopLeftRadius = radius;
            e.style.borderTopRightRadius = radius;
            e.style.borderBottomLeftRadius = radius;
            e.style.borderBottomRightRadius = radius;
        }

        private static void SetBorder(VisualElement e, Color color, float width)
        {
            e.style.borderLeftColor = color;
            e.style.borderRightColor = color;
            e.style.borderTopColor = color;
            e.style.borderBottomColor = color;
            e.style.borderLeftWidth = width;
            e.style.borderRightWidth = width;
            e.style.borderTopWidth = width;
            e.style.borderBottomWidth = width;
        }
    }
}
