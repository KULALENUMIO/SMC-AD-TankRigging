using UnityEngine;

namespace TankRig
{
    /// <summary>Ray / sphere casts that skip the tank's own colliders (everything under <c>ignore</c>).</summary>
    public static class TankPhysics
    {
        static readonly RaycastHit[] buffer = new RaycastHit[32];

        public static bool Raycast(Vector3 origin, Vector3 dir, float distance, int mask, Transform ignore, out RaycastHit hit)
        {
            int n = Physics.RaycastNonAlloc(origin, dir, buffer, distance, mask, QueryTriggerInteraction.Ignore);
            return Nearest(n, ignore, out hit);
        }

        public static bool SphereCast(Vector3 origin, float radius, Vector3 dir, float distance, int mask, Transform ignore, out RaycastHit hit)
        {
            int n = Physics.SphereCastNonAlloc(origin, radius, dir, buffer, distance, mask, QueryTriggerInteraction.Ignore);
            return Nearest(n, ignore, out hit, true);
        }

        static bool Nearest(int n, Transform ignore, out RaycastHit hit, bool skipStartOverlap = false)
        {
            hit = default;
            float best = float.MaxValue;
            bool found = false;
            for (int i = 0; i < n; i++)
            {
                var h = buffer[i];
                if (ignore && h.collider.transform.IsChildOf(ignore)) continue;
                if (skipStartOverlap && h.distance <= 0f && h.point == Vector3.zero) continue;
                if (h.distance < best)
                {
                    best = h.distance;
                    hit = h;
                    found = true;
                }
            }
            return found;
        }
    }
}
