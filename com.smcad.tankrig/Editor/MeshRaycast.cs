using System.Collections.Generic;
using UnityEngine;

namespace TankRig.Editor
{
    /// <summary>Ray against mesh triangles (no colliders needed), for clicking points onto the model in the Scene view.</summary>
    public static class MeshRaycast
    {
        public struct Hit
        {
            public Vector3 point;
            public Vector3 normal;
            public float distance;
            public Transform transform;
        }

        public static bool Raycast(Ray ray, IEnumerable<MeshFilter> filters, out Hit hit)
        {
            hit = new Hit { distance = float.MaxValue };
            bool any = false;
            foreach (var mf in filters)
            {
                if (!mf || !mf.sharedMesh) continue;
                var r = mf.GetComponent<Renderer>();
                if (r && !r.bounds.IntersectRay(ray)) continue;
                var mesh = mf.sharedMesh;
                var w2l = mf.transform.worldToLocalMatrix;
                Vector3 o = w2l.MultiplyPoint3x4(ray.origin);
                Vector3 d = w2l.MultiplyVector(ray.direction);
                var v = mesh.vertices;
                var tris = mesh.triangles;
                float bestT = float.MaxValue;
                int bestI = -1;
                for (int i = 0; i + 2 < tris.Length; i += 3)
                {
                    if (Intersect(o, d, v[tris[i]], v[tris[i + 1]], v[tris[i + 2]], out float t) && t < bestT)
                    {
                        bestT = t;
                        bestI = i;
                    }
                }
                if (bestI < 0) continue;
                Vector3 wp = mf.transform.TransformPoint(o + d * bestT);
                float dist = Vector3.Distance(ray.origin, wp);
                if (dist >= hit.distance) continue;
                Vector3 a = v[tris[bestI]], b = v[tris[bestI + 1]], c = v[tris[bestI + 2]];
                Vector3 n = mf.transform.TransformDirection(Vector3.Cross(b - a, c - a)).normalized;
                if (Vector3.Dot(n, ray.direction) > 0f) n = -n;
                hit = new Hit { point = wp, normal = n, distance = dist, transform = mf.transform };
                any = true;
            }
            return any;
        }

        static bool Intersect(Vector3 o, Vector3 d, Vector3 a, Vector3 b, Vector3 c, out float t)
        {
            t = 0f;
            Vector3 e1 = b - a, e2 = c - a;
            Vector3 p = Vector3.Cross(d, e2);
            float det = Vector3.Dot(e1, p);
            if (Mathf.Abs(det) < 1e-12f) return false;
            float inv = 1f / det;
            Vector3 s = o - a;
            float u = Vector3.Dot(s, p) * inv;
            if (u < 0f || u > 1f) return false;
            Vector3 q = Vector3.Cross(s, e1);
            float w = Vector3.Dot(d, q) * inv;
            if (w < 0f || u + w > 1f) return false;
            t = Vector3.Dot(e2, q) * inv;
            return t > 1e-6f;
        }
    }
}
