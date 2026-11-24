using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace TankRig.Sights
{
    /// <summary>
    /// Builds the sight's symbology as one mesh in "overlay units": origin at the centre, +y up, the overlay is
    /// 1080 units high (width = 1080 × aspect). Lines are anti-aliased in the shader, text uses the built-in font.
    /// Colours are given in sRGB (like the inspector) and stored linear.
    /// </summary>
    public sealed class SightPainter
    {
        public const float Height = 1080f;

        // per vertex: xy = position, uv: x,y = texture / across-line coordinate, z = kind, w = line edge
        const float KindLine = 0f, KindText = 1f, KindFill = 2f, KindSensor = 3f;

        readonly List<Vector3> pos = new List<Vector3>(4096);
        readonly List<Color> col = new List<Color>(4096);
        readonly List<Vector4> uv = new List<Vector4>(4096);
        readonly List<int> idx = new List<int>(8192);
        readonly List<Vector2> tmp = new List<Vector2>(128);

        /// <summary>Overlay pixels per unit (overlay pixel height / 1080).</summary>
        public float PxPerUnit { get; private set; } = 1f;
        public float Aspect { get; private set; } = 16f / 9f;
        public float HalfWidth => Height * 0.5f * Aspect;
        Font font;
        // looked up on first use: GetBuiltinResource may not run in a MonoBehaviour's field initializer
        public Font Font => font ? font : font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

        public void Begin(int overlayWidthPx, int overlayHeightPx)
        {
            pos.Clear(); col.Clear(); uv.Clear(); idx.Clear();
            PxPerUnit = Mathf.Max(0.1f, overlayHeightPx / Height);
            Aspect = overlayWidthPx / (float)Mathf.Max(1, overlayHeightPx);
        }

        public void Apply(Mesh mesh)
        {
            mesh.Clear();
            if (pos.Count > 65000) mesh.indexFormat = IndexFormat.UInt32;
            mesh.SetVertices(pos);
            mesh.SetColors(col);
            mesh.SetUVs(0, uv);
            mesh.SetTriangles(idx, 0, false);
            mesh.bounds = new Bounds(Vector3.zero, new Vector3(1e5f, 1e5f, 1f));
        }

        static Color Lin(Color c) { var l = c.linear; l.a = c.a; return l; }

        void Vert(Vector2 p, Color c, Vector4 t)
        {
            pos.Add(new Vector3(p.x, p.y, 0f));
            col.Add(c);
            uv.Add(t);
        }

        void QuadIdx()
        {
            int b = pos.Count - 4;
            idx.Add(b); idx.Add(b + 1); idx.Add(b + 2);
            idx.Add(b); idx.Add(b + 2); idx.Add(b + 3);
        }

        // ------------------------------------------------------------------ lines

        /// <summary>A straight line; <paramref name="width"/> in units (at least one pixel is drawn).</summary>
        public void Line(Vector2 a, Vector2 b, float width, Color c)
        {
            Vector2 d = b - a;
            float len = d.magnitude;
            if (len < 1e-4f) return;
            d /= len;
            Vector2 n = new Vector2(-d.y, d.x);
            float halfPx = Mathf.Max(0.5f, width * PxPerUnit * 0.5f);
            float outerPx = halfPx + 1f;                 // one pixel of feather outside the edge
            float h = outerPx / PxPerUnit;
            float edge = halfPx / outerPx;
            Vector2 ext = d * (0.5f / PxPerUnit);
            a -= ext; b += ext;
            var lc = Lin(c);
            Vert(a - n * h, lc, new Vector4(0f, -1f, KindLine, edge));
            Vert(b - n * h, lc, new Vector4(1f, -1f, KindLine, edge));
            Vert(b + n * h, lc, new Vector4(1f, 1f, KindLine, edge));
            Vert(a + n * h, lc, new Vector4(0f, 1f, KindLine, edge));
            QuadIdx();
        }

        public void Polyline(IList<Vector2> pts, bool closed, float width, Color c)
        {
            for (int i = 0; i + 1 < pts.Count; i++) Line(pts[i], pts[i + 1], width, c);
            if (closed && pts.Count > 2) Line(pts[pts.Count - 1], pts[0], width, c);
        }

        int Segments(float r) => Mathf.Clamp(Mathf.CeilToInt(r * PxPerUnit * 0.6f), 12, 160);

        public void Circle(Vector2 c, float r, float width, Color color) => Arc(c, r, 0f, 360f, width, color);

        /// <summary>Arc from <paramref name="a0"/> to <paramref name="a1"/> degrees (0 = up, clockwise).</summary>
        public void Arc(Vector2 c, float r, float a0, float a1, float width, Color color)
        {
            int n = Mathf.Max(2, Mathf.CeilToInt(Segments(r) * Mathf.Abs(a1 - a0) / 360f));
            tmp.Clear();
            for (int i = 0; i <= n; i++)
            {
                float a = Mathf.Lerp(a0, a1, i / (float)n) * Mathf.Deg2Rad;
                tmp.Add(c + new Vector2(Mathf.Sin(a), Mathf.Cos(a)) * r);
            }
            Polyline(tmp, false, width, color);
        }

        public void Box(Rect r, float width, Color c)
        {
            Line(new Vector2(r.xMin, r.yMin), new Vector2(r.xMax, r.yMin), width, c);
            Line(new Vector2(r.xMax, r.yMin), new Vector2(r.xMax, r.yMax), width, c);
            Line(new Vector2(r.xMax, r.yMax), new Vector2(r.xMin, r.yMax), width, c);
            Line(new Vector2(r.xMin, r.yMax), new Vector2(r.xMin, r.yMin), width, c);
        }

        /// <summary>Corner brackets of a rectangle (a tracking gate).</summary>
        public void Brackets(Rect r, float arm, float width, Color c)
        {
            Line(new Vector2(r.xMin, r.yMin), new Vector2(r.xMin + arm, r.yMin), width, c);
            Line(new Vector2(r.xMin, r.yMin), new Vector2(r.xMin, r.yMin + arm), width, c);
            Line(new Vector2(r.xMax, r.yMin), new Vector2(r.xMax - arm, r.yMin), width, c);
            Line(new Vector2(r.xMax, r.yMin), new Vector2(r.xMax, r.yMin + arm), width, c);
            Line(new Vector2(r.xMax, r.yMax), new Vector2(r.xMax - arm, r.yMax), width, c);
            Line(new Vector2(r.xMax, r.yMax), new Vector2(r.xMax, r.yMax - arm), width, c);
            Line(new Vector2(r.xMin, r.yMax), new Vector2(r.xMin + arm, r.yMax), width, c);
            Line(new Vector2(r.xMin, r.yMax), new Vector2(r.xMin, r.yMax - arm), width, c);
        }

        // ------------------------------------------------------------------ fills

        public void Fill(Rect r, Color c)
        {
            var lc = Lin(c);
            var t = new Vector4(0f, 0f, KindFill, 0f);
            Vert(new Vector2(r.xMin, r.yMin), lc, t);
            Vert(new Vector2(r.xMax, r.yMin), lc, t);
            Vert(new Vector2(r.xMax, r.yMax), lc, t);
            Vert(new Vector2(r.xMin, r.yMax), lc, t);
            QuadIdx();
        }

        /// <summary>Fills everything outside <paramref name="hole"/> (within the overlay) with <paramref name="c"/>.</summary>
        public void FillOutside(Rect hole, Color c)
        {
            float w = HalfWidth + 2f, h = Height * 0.5f + 2f;
            Fill(Rect.MinMaxRect(-w, -h, w, hole.yMin), c);
            Fill(Rect.MinMaxRect(-w, hole.yMax, w, h), c);
            Fill(Rect.MinMaxRect(-w, hole.yMin, hole.xMin, hole.yMax), c);
            Fill(Rect.MinMaxRect(hole.xMax, hole.yMin, w, hole.yMax), c);
        }

        /// <summary>A filled convex polygon.</summary>
        public void FillPoly(IList<Vector2> pts, Color c)
        {
            if (pts.Count < 3) return;
            var lc = Lin(c);
            var t = new Vector4(0f, 0f, KindFill, 0f);
            int b = pos.Count;
            foreach (var p in pts) Vert(p, lc, t);
            for (int i = 1; i + 1 < pts.Count; i++) { idx.Add(b); idx.Add(b + i); idx.Add(b + i + 1); }
        }

        /// <summary>A filled ring from radius <paramref name="r0"/> (colour <paramref name="c0"/>) to <paramref name="r1"/> (<paramref name="c1"/>).</summary>
        public void Ring(Vector2 c, float r0, float r1, Color c0, Color c1)
        {
            int n = Segments(Mathf.Max(r0, r1));
            var l0 = Lin(c0); var l1 = Lin(c1);
            var t = new Vector4(0f, 0f, KindFill, 0f);
            int b = pos.Count;
            for (int i = 0; i <= n; i++)
            {
                float a = i / (float)n * Mathf.PI * 2f;
                var d = new Vector2(Mathf.Sin(a), Mathf.Cos(a));
                Vert(c + d * r0, l0, t);
                Vert(c + d * r1, l1, t);
            }
            for (int i = 0; i < n; i++)
            {
                int v = b + i * 2;
                idx.Add(v); idx.Add(v + 1); idx.Add(v + 3);
                idx.Add(v); idx.Add(v + 3); idx.Add(v + 2);
            }
        }

        /// <summary>Darkens everything outside a circle of radius <paramref name="r"/>, with a soft edge of <paramref name="soft"/> units.</summary>
        public void CircleMask(Vector2 c, float r, float soft, Color outside)
        {
            var clear = outside; clear.a = 0f;
            float far = Mathf.Sqrt(HalfWidth * HalfWidth + Height * Height * 0.25f) + c.magnitude + 4f;
            if (soft > 0.01f) Ring(c, r - soft, r, clear, outside);
            Ring(c, r, far, outside, outside);
        }

        /// <summary>The sensor image (drawn with the sensor settings of the frame) in <paramref name="r"/>; <paramref name="uvRect"/> picks part of it.</summary>
        public void Sensor(Rect r, Rect uvRect, Color tint)
        {
            var lc = Lin(tint);
            Vert(new Vector2(r.xMin, r.yMin), lc, new Vector4(uvRect.xMin, uvRect.yMin, KindSensor, 0f));
            Vert(new Vector2(r.xMax, r.yMin), lc, new Vector4(uvRect.xMax, uvRect.yMin, KindSensor, 0f));
            Vert(new Vector2(r.xMax, r.yMax), lc, new Vector4(uvRect.xMax, uvRect.yMax, KindSensor, 0f));
            Vert(new Vector2(r.xMin, r.yMax), lc, new Vector4(uvRect.xMin, uvRect.yMax, KindSensor, 0f));
            QuadIdx();
        }

        // ------------------------------------------------------------------ text

        /// <summary>Width of <paramref name="s"/> in units at <paramref name="size"/> units.</summary>
        public float TextWidth(string s, float size)
        {
            if (string.IsNullOrEmpty(s) || !Font) return 0f;
            int px = Mathf.Max(4, Mathf.RoundToInt(size * PxPerUnit));
            Font.RequestCharactersInTexture(s, px, FontStyle.Normal);
            float w = 0f;
            foreach (char ch in s)
                if (Font.GetCharacterInfo(ch, out var ci, px, FontStyle.Normal)) w += ci.advance;
            return w / PxPerUnit;
        }

        /// <summary>
        /// Text at <paramref name="at"/>; <paramref name="align"/>: x −1 left, 0 centre, 1 right of the point; y likewise
        /// (−1 = point is the bottom). <paramref name="outline"/> (alpha &gt; 0) draws a 1-pixel dark edge first.
        /// </summary>
        public void Text(string s, Vector2 at, float size, Color c, Vector2 align, Color outline = default)
        {
            if (string.IsNullOrEmpty(s) || !Font) return;
            if (outline.a > 0f)
            {
                float o = 1.2f / PxPerUnit;
                Text(s, at + new Vector2(o, -o), size, outline, align);
                Text(s, at + new Vector2(-o, o), size, outline, align);
                Text(s, at + new Vector2(o, o), size, outline, align);
                Text(s, at + new Vector2(-o, -o), size, outline, align);
            }
            int px = Mathf.Max(4, Mathf.RoundToInt(size * PxPerUnit));
            Font.RequestCharactersInTexture(s, px, FontStyle.Normal);
            float w = TextWidth(s, size);
            // the built-in font's cap height is about 0.7 of the size; centre on that
            float x = at.x - w * (align.x + 1f) * 0.5f;
            float baseline = at.y - size * 0.35f - align.y * size * 0.5f;
            var lc = Lin(c);
            foreach (char ch in s)
            {
                if (!Font.GetCharacterInfo(ch, out var ci, px, FontStyle.Normal)) continue;
                float x0 = x + ci.minX / PxPerUnit, x1 = x + ci.maxX / PxPerUnit;
                float y0 = baseline + ci.minY / PxPerUnit, y1 = baseline + ci.maxY / PxPerUnit;
                if (x1 > x0 && y1 > y0)
                {
                    Vert(new Vector2(x0, y0), lc, new Vector4(ci.uvBottomLeft.x, ci.uvBottomLeft.y, KindText, 0f));
                    Vert(new Vector2(x1, y0), lc, new Vector4(ci.uvBottomRight.x, ci.uvBottomRight.y, KindText, 0f));
                    Vert(new Vector2(x1, y1), lc, new Vector4(ci.uvTopRight.x, ci.uvTopRight.y, KindText, 0f));
                    Vert(new Vector2(x0, y1), lc, new Vector4(ci.uvTopLeft.x, ci.uvTopLeft.y, KindText, 0f));
                    QuadIdx();
                }
                x += ci.advance / PxPerUnit;
            }
        }
    }
}
