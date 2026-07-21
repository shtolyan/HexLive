using UnityEngine;
using UnityEngine.UIElements;

namespace HexLive.UnityPresentation.UI
{
    /// <summary>
    /// A crisp, resolution-independent icon drawn with Painter2D. Shapes are
    /// authored in a 24×24 space and scaled to the element's content rect, so
    /// they stay sharp at any size and can be recolored at runtime.
    /// </summary>
    public sealed class VectorIcon : VisualElement
    {
        public enum Kind
        {
            Hunger,
            Thirst,
            Energy,
            Comfort,
            Social,
            Thermal,
            Health,
            Think,
            HeartFill,
            Shield,
            Pause,
            Play,
            Sun,
            ChevronDown,
            ChevronUp,
            Dream
        }

        private readonly Kind _kind;
        private Color _color;

        public VectorIcon(Kind kind, Color color)
        {
            _kind = kind;
            _color = color;
            pickingMode = PickingMode.Ignore;
            generateVisualContent += OnGenerate;
        }

        public void SetColor(Color color)
        {
            _color = color;
            MarkDirtyRepaint();
        }

        private void OnGenerate(MeshGenerationContext ctx)
        {
            var rect = contentRect;
            if (rect.width < 1f || rect.height < 1f)
            {
                return;
            }

            var k = Mathf.Min(rect.width, rect.height) / 24f;
            var ox = rect.x + (rect.width - 24f * k) * 0.5f;
            var oy = rect.y + (rect.height - 24f * k) * 0.5f;

            Vector2 P(float x, float y) => new(ox + x * k, oy + y * k);

            var p = ctx.painter2D;
            p.lineWidth = 1.8f * k;
            p.lineJoin = LineJoin.Round;
            p.lineCap = LineCap.Round;
            p.strokeColor = _color;
            p.fillColor = _color;

            switch (_kind)
            {
                case Kind.Hunger: DrawHunger(p, P, k); break;
                case Kind.Thirst: DrawThirst(p, P); break;
                case Kind.Energy: DrawEnergy(p, P); break;
                case Kind.Comfort: DrawComfort(p, P); break;
                case Kind.Social: DrawSocial(p, P, k); break;
                case Kind.Thermal: DrawThermal(p, P, k); break;
                case Kind.Health: DrawHeart(p, P, false); break;
                case Kind.HeartFill: DrawHeart(p, P, true); break;
                case Kind.Shield: DrawShield(p, P); break;
                case Kind.Think: DrawThink(p, P); break;
                case Kind.Pause: DrawPause(p, P); break;
                case Kind.Play: DrawPlay(p, P); break;
                case Kind.Sun: DrawSun(p, P, k); break;
                case Kind.ChevronDown: DrawChevron(p, P, true); break;
                case Kind.ChevronUp: DrawChevron(p, P, false); break;
                case Kind.Dream: DrawDream(p, P); break;
            }
        }

        // Spec §64: a filled five-point star — the colonist's aspiration.
        private static void DrawDream(Painter2D p, Pt P)
        {
            p.BeginPath();
            p.MoveTo(P(12f, 2.8f));
            p.LineTo(P(14.29f, 8.84f));
            p.LineTo(P(20.75f, 9.16f));
            p.LineTo(P(15.71f, 13.21f));
            p.LineTo(P(17.41f, 19.44f));
            p.LineTo(P(12f, 15.9f));
            p.LineTo(P(6.59f, 19.44f));
            p.LineTo(P(8.29f, 13.21f));
            p.LineTo(P(3.25f, 9.16f));
            p.LineTo(P(9.71f, 8.84f));
            p.ClosePath();
            p.Fill();
            p.Stroke();
        }

        private static void DrawChevron(Painter2D p, Pt P, bool down)
        {
            var yTip = down ? 15.5f : 8.5f;
            var yBase = down ? 8.5f : 15.5f;
            p.BeginPath();
            p.MoveTo(P(5.5f, yBase));
            p.LineTo(P(12f, yTip));
            p.LineTo(P(18.5f, yBase));
            p.Stroke();
        }

        private delegate Vector2 Pt(float x, float y);

        private static void DrawShield(Painter2D p, Pt P)
        {
            p.BeginPath();
            p.MoveTo(P(12f, 3.2f));
            p.BezierCurveTo(P(15f, 4.9f), P(17.8f, 5.3f), P(19.6f, 5.6f));
            p.LineTo(P(19.1f, 11.3f));
            p.BezierCurveTo(P(18.7f, 16.1f), P(15.8f, 19.3f), P(12f, 21f));
            p.BezierCurveTo(P(8.2f, 19.3f), P(5.3f, 16.1f), P(4.9f, 11.3f));
            p.LineTo(P(4.4f, 5.6f));
            p.BezierCurveTo(P(6.2f, 5.3f), P(9f, 4.9f), P(12f, 3.2f));
            p.ClosePath();
            p.Stroke();

            p.BeginPath();
            p.MoveTo(P(8.2f, 12f));
            p.LineTo(P(10.8f, 14.6f));
            p.LineTo(P(16.2f, 9.1f));
            p.Stroke();
        }

        // Apple: round body + short stem.
        private static void DrawHunger(Painter2D p, Pt P, float k)
        {
            p.BeginPath();
            p.Arc(P(12f, 13.5f), 6.2f * k, Angle.Degrees(0f), Angle.Degrees(360f));
            p.Stroke();

            p.BeginPath();
            p.MoveTo(P(12f, 7.6f));
            p.LineTo(P(13f, 4.2f));
            p.Stroke();
        }

        // Teardrop.
        private static void DrawThirst(Painter2D p, Pt P)
        {
            p.BeginPath();
            p.MoveTo(P(12f, 3.5f));
            p.BezierCurveTo(P(14f, 7f), P(18.5f, 11f), P(18.5f, 15f));
            p.BezierCurveTo(P(18.5f, 18.6f), P(15.6f, 20.5f), P(12f, 20.5f));
            p.BezierCurveTo(P(8.4f, 20.5f), P(5.5f, 18.6f), P(5.5f, 15f));
            p.BezierCurveTo(P(5.5f, 11f), P(10f, 7f), P(12f, 3.5f));
            p.ClosePath();
            p.Stroke();
        }

        // Lightning bolt.
        private static void DrawEnergy(Painter2D p, Pt P)
        {
            p.BeginPath();
            p.MoveTo(P(13f, 2f));
            p.LineTo(P(4f, 14f));
            p.LineTo(P(11f, 14f));
            p.LineTo(P(10f, 22f));
            p.LineTo(P(20f, 9f));
            p.LineTo(P(13f, 9f));
            p.ClosePath();
            p.Stroke();
        }

        // Armchair / couch silhouette.
        private static void DrawComfort(Painter2D p, Pt P)
        {
            p.BeginPath();
            p.MoveTo(P(6f, 12f));
            p.LineTo(P(6f, 8.5f));
            p.LineTo(P(18f, 8.5f));
            p.LineTo(P(18f, 12f));
            p.Stroke();

            p.BeginPath();
            p.MoveTo(P(4.5f, 12f));
            p.LineTo(P(4.5f, 16.5f));
            p.LineTo(P(19.5f, 16.5f));
            p.LineTo(P(19.5f, 12f));
            p.Stroke();

            p.BeginPath();
            p.MoveTo(P(6f, 16.5f));
            p.LineTo(P(6f, 18.5f));
            p.MoveTo(P(18f, 16.5f));
            p.LineTo(P(18f, 18.5f));
            p.Stroke();
        }

        // Two people.
        private static void DrawSocial(Painter2D p, Pt P, float k)
        {
            p.BeginPath();
            p.Arc(P(9f, 8f), 3f * k, Angle.Degrees(0f), Angle.Degrees(360f));
            p.Stroke();

            p.BeginPath();
            p.Arc(P(16.5f, 9.5f), 2.4f * k, Angle.Degrees(0f), Angle.Degrees(360f));
            p.Stroke();

            p.BeginPath();
            p.MoveTo(P(4f, 19f));
            p.BezierCurveTo(P(4f, 15.7f), P(6.3f, 14f), P(9f, 14f));
            p.BezierCurveTo(P(11.7f, 14f), P(14f, 15.7f), P(14f, 19f));
            p.Stroke();

            p.BeginPath();
            p.MoveTo(P(14.8f, 18.4f));
            p.BezierCurveTo(P(15f, 15.9f), P(16.5f, 14.8f), P(18.2f, 14.8f));
            p.BezierCurveTo(P(19.9f, 14.8f), P(21f, 16f), P(21f, 18.4f));
            p.Stroke();
        }

        // Thermometer: stem + filled bulb.
        private static void DrawThermal(Painter2D p, Pt P, float k)
        {
            p.BeginPath();
            p.MoveTo(P(9.7f, 13.6f));
            p.LineTo(P(9.7f, 6f));
            p.BezierCurveTo(P(9.7f, 4f), P(13.3f, 4f), P(13.3f, 6f));
            p.LineTo(P(13.3f, 13.6f));
            p.Stroke();

            p.BeginPath();
            p.Arc(P(11.5f, 16.6f), 3.2f * k, Angle.Degrees(0f), Angle.Degrees(360f));
            p.Fill();
        }

        // Heart (outline or filled).
        private static void DrawHeart(Painter2D p, Pt P, bool fill)
        {
            p.BeginPath();
            p.MoveTo(P(12f, 20f));
            p.BezierCurveTo(P(4.5f, 14.8f), P(4f, 10.5f), P(7f, 8.5f));
            p.BezierCurveTo(P(9.2f, 7.1f), P(11.2f, 8f), P(12f, 9.6f));
            p.BezierCurveTo(P(12.8f, 8f), P(14.8f, 7.1f), P(17f, 8.5f));
            p.BezierCurveTo(P(20f, 10.5f), P(19.5f, 14.8f), P(12f, 20f));
            p.ClosePath();
            if (fill)
            {
                p.Fill();
            }
            else
            {
                p.Stroke();
            }
        }

        // Two rounded bars.
        private static void DrawPause(Painter2D p, Pt P)
        {
            FillRoundedRect(p, P, 6f, 4.5f, 3.6f, 15f);
            FillRoundedRect(p, P, 13.9f, 4.5f, 3.6f, 15f);
        }

        // Right-pointing triangle.
        private static void DrawPlay(Painter2D p, Pt P)
        {
            p.BeginPath();
            p.MoveTo(P(7f, 4.5f));
            p.LineTo(P(19f, 12f));
            p.LineTo(P(7f, 19.5f));
            p.ClosePath();
            p.Fill();
        }

        private static void FillRoundedRect(Painter2D p, Pt P, float x, float y, float w, float h)
        {
            var r = 1.2f;
            p.BeginPath();
            p.MoveTo(P(x + r, y));
            p.LineTo(P(x + w - r, y));
            p.BezierCurveTo(P(x + w, y), P(x + w, y), P(x + w, y + r));
            p.LineTo(P(x + w, y + h - r));
            p.BezierCurveTo(P(x + w, y + h), P(x + w, y + h), P(x + w - r, y + h));
            p.LineTo(P(x + r, y + h));
            p.BezierCurveTo(P(x, y + h), P(x, y + h), P(x, y + h - r));
            p.LineTo(P(x, y + r));
            p.BezierCurveTo(P(x, y), P(x, y), P(x + r, y));
            p.ClosePath();
            p.Fill();
        }

        // Sun: disc + 8 rays.
        private static void DrawSun(Painter2D p, Pt P, float k)
        {
            p.BeginPath();
            p.Arc(P(12f, 12f), 4.4f * k, Angle.Degrees(0f), Angle.Degrees(360f));
            p.Stroke();

            for (var i = 0; i < 8; i++)
            {
                var a = Mathf.Deg2Rad * (45f * i);
                var cos = Mathf.Cos(a);
                var sin = Mathf.Sin(a);
                p.BeginPath();
                p.MoveTo(P(12f + cos * 7f, 12f + sin * 7f));
                p.LineTo(P(12f + cos * 9.5f, 12f + sin * 9.5f));
                p.Stroke();
            }
        }

        // Thought bubble.
        private static void DrawThink(Painter2D p, Pt P)
        {
            p.BeginPath();
            p.MoveTo(P(7.5f, 7f));
            p.LineTo(P(16.5f, 7f));
            p.BezierCurveTo(P(18.8f, 7f), P(18.8f, 13.5f), P(16.5f, 13.5f));
            p.LineTo(P(11.5f, 13.5f));
            p.LineTo(P(8f, 16.5f));
            p.LineTo(P(8.6f, 13.5f));
            p.LineTo(P(7.5f, 13.5f));
            p.BezierCurveTo(P(5.2f, 13.5f), P(5.2f, 7f), P(7.5f, 7f));
            p.ClosePath();
            p.Stroke();
        }
    }
}
