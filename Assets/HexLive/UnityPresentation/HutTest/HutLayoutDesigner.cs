#nullable enable
using System;
using System.Collections.Generic;
using System.Text;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using HexLive.UnityPresentation.Bootstrap;
using HexLive.UnityPresentation.Environment;
using HexLive.UnityPresentation.Views;
using UnityEngine;
using UnityEngine.InputSystem;

namespace HexLive.UnityPresentation.HutTest
{

/// <summary>Player-facing in-scene authoring fixture for the one-hex blueprint.</summary>
public sealed class HutLayoutDesigner : MonoBehaviour
{
    private const string DraftKey = "HexLive.HutLayoutDraft.v1";
    private const float PanelWidth = 286f;
    private const float PanelHeight = 500f;
    private const float PanelMargin = 12f;
    private readonly BuildingElementKind[] _bays = new BuildingElementKind[12];
    private readonly List<FurnitureDraft> _furniture = new();
    private readonly List<GameObject> _markers = new();
    private SimulationRunnerBehaviour? _runner;
    private Camera? _camera;
    private Transform? _source;
    private GameObject? _preview;
    private Transform? _furnitureRoot;
    private Material? _markerMaterial;
    private Tool _tool;
    private bool _painting;
    private int _lastBay = -1;
    private int _selected = -1;
    private string _status = "Выберите инструмент.";

    [Serializable] private sealed class DraftData
    {
        public int[] bayKinds = Array.Empty<int>();
        public FurnitureDraft[] furniture = Array.Empty<FurnitureDraft>();
    }
    [Serializable] private sealed class FurnitureDraft
    {
        public string type = "bed";
        public int junction;
        public float localX;
        public float localZ;
        public float rotationDegrees;
    }
    private enum Tool { Select, Move, Wall, Window, Door, Bed, Hearth, Delete }

    private void Start()
    {
        _runner = FindAnyObjectByType<SimulationRunnerBehaviour>();
        _camera = Camera.main;
        for (var i = 0; i < 12; i++) _bays[i] = BuildingRules.HutBayKind(i);
        Load();
    }

    private void Update()
    {
        if (_preview == null) Initialize();
        if (_preview == null || _camera == null) return;
        if (_runner != null && !_runner.IsPaused) _runner.TogglePause();
        var mouse = Mouse.current;
        if (mouse == null) return;
        var screen = mouse.position.ReadValue();
        if (screen.x < PanelWidth + PanelMargin * 2f && screen.y < PanelHeight + PanelMargin * 2f) return;
        if (!TryPoint(screen, out var point)) return;
        if (_painting && IsBayTool(_tool)) Paint(point);
        if (!mouse.leftButton.wasPressedThisFrame) return;
        if (IsBayTool(_tool))
        {
            if (!_painting) { _painting = true; _lastBay = -1; Paint(point); _status = "Ведём стену. Второй клик — закончить."; }
            else { Paint(point); _painting = false; _lastBay = -1; _status = "Линия завершена."; }
        }
        else if (_tool is Tool.Bed or Tool.Hearth) Place(point, _tool == Tool.Bed ? "bed" : "hearth");
        else if (_tool == Tool.Move) MoveSelected(point);
        else Select(point, _tool == Tool.Delete);
    }

    private void Initialize()
    {
        foreach (var hut in FindObjectsByType<HutAssembly>(FindObjectsSortMode.None))
            if (!hut.name.Contains("designer preview")) { _source = hut.transform; break; }
        if (_source == null) return;
        foreach (var r in _source.GetComponentsInChildren<Renderer>(true)) r.enabled = false;
        foreach (var view in WorldObjectView.All)
            if (view != null && Vector3.Distance(view.transform.position, _source.position) < 2f)
                foreach (var r in view.GetComponentsInChildren<Renderer>(true)) r.enabled = false;
        Rebuild();
        _status = "Конструктор включён. Симуляция на паузе.";
    }

    private void Rebuild()
    {
        if (_source == null) return;
        if (_preview != null) Destroy(_preview);
        _preview = HutAssembly.BuildDesignerPreview(_bays);
        _preview.transform.SetPositionAndRotation(_source.position, _source.rotation);
        _furnitureRoot = new GameObject("Designer furniture").transform;
        _furnitureRoot.SetParent(_preview.transform, false);
        foreach (var item in _furniture)
        {
            var go = item.type == "bed" ? HutFurnitureFactory.BuildBed() : HutFurnitureFactory.BuildHearth();
            if (go == null) continue;
            go.name = $"Designer {item.type} junction {item.junction}";
            go.transform.SetParent(_furnitureRoot, false);
            go.transform.localPosition = new Vector3(item.localX, HutAssembly.FloorSurfaceLift, item.localZ);
            go.transform.localRotation = Quaternion.Euler(0f, -item.rotationDegrees, 0f);
        }
        BuildMarkers();
    }

    private void BuildMarkers()
    {
        if (_preview == null) return;
        _markers.Clear();
        if (_markerMaterial == null)
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            _markerMaterial = new Material(shader) { color = new Color(0.20f, 0.78f, 0.53f, 0.72f) };
        }
        foreach (var node in HexPointLayout.GetInteriorTemplates())
        {
            var marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            marker.name = $"junction {node.Slot}";
            marker.transform.SetParent(_preview.transform, false);
            marker.transform.localPosition = new Vector3(node.Offset.X, HutAssembly.FloorSurfaceLift + .02f, node.Offset.Y);
            marker.transform.localScale = Vector3.one * .055f;
            marker.GetComponent<Renderer>().sharedMaterial = _markerMaterial;
            Destroy(marker.GetComponent<Collider>());
            _markers.Add(marker);
        }
    }

    private bool TryPoint(Vector2 screen, out Vector3 local)
    {
        local = default;
        if (_camera == null || _preview == null) return false;
        var plane = new Plane(Vector3.up, _preview.transform.position + Vector3.up * HutAssembly.FloorSurfaceLift);
        var ray = _camera.ScreenPointToRay(screen);
        if (!plane.Raycast(ray, out var distance)) return false;
        local = _preview.transform.InverseTransformPoint(ray.GetPoint(distance));
        return true;
    }

    private void Paint(Vector3 point)
    {
        var bay = NearestBay(point);
        if (bay < 0 || bay == _lastBay) return;
        _lastBay = bay;
        var kind = _tool == Tool.Window ? BuildingElementKind.Window : _tool == Tool.Door ? BuildingElementKind.Door : BuildingElementKind.Wall;
        if (kind == BuildingElementKind.Door)
            for (var i = 0; i < 12; i++) if (_bays[i] == BuildingElementKind.Door) _bays[i] = BuildingElementKind.Wall;
        _bays[bay] = kind;
        Save(false); Rebuild();
    }

    private static int NearestBay(Vector3 point)
    {
        var best = -1; var bestSq = float.MaxValue;
        for (var bay = 0; bay < 12; bay++)
        {
            BayCenter(bay, out var center);
            var sq = (new Vector2(point.x - center.x, point.z - center.z)).sqrMagnitude;
            if (sq < bestSq) { bestSq = sq; best = bay; }
        }
        return bestSq < .49f ? best : -1;
    }

    private void Place(Vector3 point, string type)
    {
        var nearest = NearestNode(point);
        if (nearest == null) return;
        if (type == "hearth") _furniture.RemoveAll(f => f.type == "hearth");
        var item = new FurnitureDraft { type = type, junction = nearest.Value.Slot,
            localX = nearest.Value.Offset.X, localZ = nearest.Value.Offset.Y };
        _furniture.Add(item); _selected = _furniture.Count - 1;
        Save(false); Rebuild();
        _status = type == "bed" ? "Кровать поставлена. Поворот — ±60°." : "Очаг поставлен.";
    }

    private static JunctionTemplate? NearestNode(Vector3 point)
    {
        JunctionTemplate? best = null; var bestSq = float.MaxValue;
        foreach (var node in HexPointLayout.GetInteriorTemplates())
        {
            var sq = (point.x - node.Offset.X) * (point.x - node.Offset.X) + (point.z - node.Offset.Y) * (point.z - node.Offset.Y);
            if (sq < bestSq) { bestSq = sq; best = node; }
        }
        return bestSq < .11f ? best : null;
    }

    private void Select(Vector3 point, bool delete)
    {
        var best = -1; var bestSq = float.MaxValue;
        for (var i = 0; i < _furniture.Count; i++)
        {
            var f = _furniture[i]; var sq = (point.x-f.localX)*(point.x-f.localX)+(point.z-f.localZ)*(point.z-f.localZ);
            if (sq < bestSq) { bestSq = sq; best = i; }
        }
        if (best < 0 || bestSq > .43f) return;
        if (delete) { _furniture.RemoveAt(best); _selected = -1; Save(false); Rebuild(); }
        else { _selected = best; _status = $"Выбрано: {_furniture[best].type}, {_furniture[best].rotationDegrees:0}°."; }
    }

    private void MoveSelected(Vector3 point)
    {
        if (_selected < 0 || _selected >= _furniture.Count)
        {
            _status = "Сначала выберите предмет.";
            return;
        }
        var nearest = NearestNode(point);
        if (nearest == null) return;
        var item = _furniture[_selected];
        item.junction = nearest.Value.Slot;
        item.localX = nearest.Value.Offset.X;
        item.localZ = nearest.Value.Offset.Y;
        Save(false);
        Rebuild();
        _status = $"{item.type} перемещён в junction {item.junction}. Можно двигать дальше.";
    }

    private void Rotate(float delta)
    {
        if (_selected < 0 || _selected >= _furniture.Count || _furniture[_selected].type != "bed") { _status = "Сначала выберите кровать."; return; }
        var f = _furniture[_selected]; f.rotationDegrees = (f.rotationDegrees + delta + 360f) % 360f;
        Save(false); Rebuild(); _status = $"Поворот кровати: {f.rotationDegrees:0}°.";
    }

    private void SetTool(Tool tool)
    {
        _tool = tool; _painting = false; _lastBay = -1;
        _status = IsBayTool(tool) ? "Первый клик начинает линию по периметру."
            : tool is Tool.Bed or Tool.Hearth ? "Кликните по зелёному junction."
            : tool == Tool.Move ? "Кликните по новой junction-точке."
            : "Кликните по предмету.";
    }

    private void Save(bool announce)
    {
        var data = new DraftData { bayKinds = Array.ConvertAll(_bays, k => (int)k), furniture = _furniture.ToArray() };
        PlayerPrefs.SetString(DraftKey, JsonUtility.ToJson(data, true)); PlayerPrefs.Save();
        if (announce) { Debug.Log($"[HutDesigner][SAVED]\n{Export()}", this); _status = "Сохранено. Скажи Codex, что закончила."; }
    }

    private void Load()
    {
        if (!PlayerPrefs.HasKey(DraftKey)) return;
        var data = JsonUtility.FromJson<DraftData>(PlayerPrefs.GetString(DraftKey));
        if (data?.bayKinds?.Length == 12) for (var i = 0; i < 12; i++) _bays[i] = (BuildingElementKind)data.bayKinds[i];
        if (data?.furniture != null) _furniture.AddRange(data.furniture);
    }

    private string Export()
    {
        var sb = new StringBuilder("{\n  \"version\": 2,\n  \"bays\": [");
        for (var i=0;i<12;i++) { if(i>0) sb.Append(','); sb.Append($"\n    {{\"index\":{i},\"edge\":{i/2},\"half\":{i%2},\"kind\":\"{_bays[i].ToString().ToLowerInvariant()}\"}}"); }
        sb.Append("\n  ],\n  \"furniture\": [");
        for(var i=0;i<_furniture.Count;i++){var f=_furniture[i];if(i>0)sb.Append(',');sb.Append($"\n    {{\"type\":\"{f.type}\",\"junction\":{f.junction},\"localX\":{f.localX:0.####},\"localZ\":{f.localZ:0.####},\"rotationDegrees\":{f.rotationDegrees:0}}}");}
        return sb.Append("\n  ]\n}").ToString();
    }

    private void OnGUI()
    {
        var panelTop = Mathf.Max(PanelMargin, Screen.height - PanelHeight - PanelMargin);
        GUI.BeginGroup(new Rect(0f, panelTop, PanelWidth + PanelMargin * 2f, PanelHeight));
        GUI.Box(new Rect(12,0,PanelWidth,PanelHeight),""); GUI.Label(new Rect(28,12,250,24),"КОНСТРУКТОР ХИЖИНЫ 1×1"); GUI.Label(new Rect(28,38,250,42),_status);
        var y=86f; Button(Tool.Select,"Выбрать",28,y); Button(Tool.Move,"Переместить",156,y); y+=38;
        Button(Tool.Delete,"Удалить",28,y); y+=38;
        Button(Tool.Wall,"Стена",28,y); Button(Tool.Window,"Окно",156,y); y+=38;
        Button(Tool.Door,"Дверь",28,y); Button(Tool.Bed,"Кровать",156,y); y+=38; Button(Tool.Hearth,"Очаг",28,y); y+=48;
        GUI.Label(new Rect(28,y,240,22),"Выбранная кровать"); y+=25;
        if(GUI.Button(new Rect(28,y,116,30),"↶ 60°"))Rotate(-60); if(GUI.Button(new Rect(156,y,116,30),"↷ 60°"))Rotate(60); y+=44;
        GUI.Label(new Rect(28,y,240,44),"Пролёты: клик — вести,\nвторой клик — закончить."); y+=55;
        if(GUI.Button(new Rect(28,y,244,34),"СОХРАНИТЬ РАСКЛАДКУ"))Save(true);
        GUI.EndGroup();
    }

    private void Button(Tool tool,string label,float x,float y){var old=GUI.color;if(_tool==tool)GUI.color=new Color(.48f,.85f,.62f);if(GUI.Button(new Rect(x,y,116,30),label))SetTool(tool);GUI.color=old;}
    private static bool IsBayTool(Tool t)=>t is Tool.Wall or Tool.Window or Tool.Door;
    private static void BayCenter(int bay,out Vector3 center){var edge=bay/2;var half=bay%2;var a0=(90+edge*60)*Mathf.Deg2Rad;var a1=(90+(edge+1)*60)*Mathf.Deg2Rad;var p0=new Vector3(Mathf.Cos(a0)*1.5f,0,Mathf.Sin(a0)*1.5f);var p1=new Vector3(Mathf.Cos(a1)*1.5f,0,Mathf.Sin(a1)*1.5f);center=Vector3.Lerp(p0,p1,half==0?.25f:.75f);}
}
}
