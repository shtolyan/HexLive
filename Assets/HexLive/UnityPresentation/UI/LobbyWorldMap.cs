using System;
using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Common;
using HexLive.Simulation.Spatial;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace HexLive.UnityPresentation.UI
{
public sealed class LobbyWorldMap : VisualElement
{
    private readonly JObject _preview;
    private readonly WorldCreationConfig _config;
    private readonly Action<Faction> _select;
    private Vector2 _center, _pan, _start, _previous;
    private float _extentX, _extentY, _zoom = 1;
    private bool _dragging;
    public LobbyWorldMap(JObject preview, WorldCreationConfig config, Action<Faction> select)
    {
        _preview = preview; _config = config; _select = select;
        var points = preview["tiles"].Select(Point).ToArray();
        var min = new Vector2(points.Min(p => p.x), points.Min(p => p.y));
        var max = new Vector2(points.Max(p => p.x), points.Max(p => p.y));
        _center = (min + max) * .5f; _extentX = max.x - min.x + 10; _extentY = max.y - min.y + 10;
        generateVisualContent += Draw;
        RegisterCallback<WheelEvent>(e => { _zoom = Mathf.Clamp(_zoom * Mathf.Exp(-e.delta.y * .08f), .5f, 12); MarkDirtyRepaint(); e.StopPropagation(); });
        RegisterCallback<PointerDownEvent>(e => { _dragging = true; _start = _previous = e.localPosition; this.CapturePointer(e.pointerId); });
        RegisterCallback<PointerMoveEvent>(e =>
        {
            if (!_dragging) return;
            Vector2 current = e.localPosition; _pan += current - _previous; _previous = current; MarkDirtyRepaint();
        });
        RegisterCallback<PointerUpEvent>(e =>
        {
            _dragging = false; this.ReleasePointer(e.pointerId);
            if (Vector2.Distance(_start, e.localPosition) > 5) return;
            foreach (var camp in _preview["camps"])
                if (Vector2.Distance(Screen(Point(camp)), e.localPosition) < 18) { _select((Faction)camp.Value<int>("faction")); break; }
            MarkDirtyRepaint();
        });
        RegisterCallback<PointerCaptureOutEvent>(_ => _dragging = false);
        schedule.Execute(MarkDirtyRepaint).Every(300);
    }
    private static Vector2 Point(JToken tile)
    {
        var p = HexSpatialMath.TileToWorld(new TileCoord(tile.Value<int>("q"), tile.Value<int>("r")));
        return new Vector2(p.X, -p.Y);
    }
    private float Scale => Mathf.Min(contentRect.width / _extentX, contentRect.height / _extentY) * _zoom;
    private Vector2 Screen(Vector2 p) => (p - _center) * Scale + contentRect.size * .5f + _pan;
    private void Draw(MeshGenerationContext context)
    {
        var painter = context.painter2D;
        foreach (var tile in _preview["tiles"])
        {
            var position = Screen(Point(tile));
            if (!contentRect.Contains(position)) continue;
            painter.fillColor = tile.Value<bool>("water") ? new Color(.08f,.25f,.34f) : Color.Lerp(new Color(.2f,.34f,.22f), new Color(.52f,.48f,.31f), tile.Value<int>("elevation") / 5f);
            painter.BeginPath();
            for (var i = 0; i < 6; i++)
            {
                var angle = (30 + i * 60) * Mathf.Deg2Rad;
                var vertex = position + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * HexSpatialMath.HexRadius * Scale;
                if (i == 0) painter.MoveTo(vertex); else painter.LineTo(vertex);
            }
            painter.ClosePath(); painter.Fill();
        }
        foreach (var camp in _preview["camps"])
        {
            var faction = (Faction)camp.Value<int>("faction"); var settings = _config.Camp(faction);
            painter.fillColor = settings?.Enabled != true ? Color.gray : faction == _config.PlayerCamp ? new Color(.95f,.74f,.25f) : faction == Faction.Outsiders ? new Color(.85f,.25f,.25f) : new Color(.4f,.7f,.95f);
            painter.BeginPath(); painter.Arc(Screen(Point(camp)), 10, Angle.Degrees(0), Angle.Degrees(360)); painter.Fill();
        }
    }
}
}
