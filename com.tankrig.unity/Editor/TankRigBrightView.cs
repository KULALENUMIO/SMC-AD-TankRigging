// SPDX-License-Identifier: MIT
// Tank Rigging: how the scene view looks while placing points. A copy of SMC-DETAILS' TankRigBrightView.cs (kept separate so
// the plugin does not depend on SMC-AD; keep the two in step).
//   明るく表示: the scene view's own lighting switch is turned off while points are being placed. HDRP then draws the scene
//              view as "MatCap" (even light from the view, no shadows, no exposure); by default without the material
//              colours (HDRP's "MatCap Mode Mix Albedo" preference is switched off meanwhile), so even a near-black
//              vehicle shows its shape. Only the scene view changes, and everything comes back when placing ends.
//   色を混ぜる: keep the material colours in that view.
// The MatCap colour switch uses the same keys as SMC-DETAILS, so whichever is used last puts HDRP's preference back.
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;

namespace TankRig.Editor
{
    public static class TankRigBrightView
    {
        const string BrightKey = "TankRig.Bright", EdgesKey = "TankRig.Edges";
        const string Holder = "rig";
        static readonly Dictionary<SceneView, bool> saved = new Dictionary<SceneView, bool>();

        public static bool Bright
        {
            get => EditorPrefs.GetBool(BrightKey, true);
            set { if (value == Bright) return; EditorPrefs.SetBool(BrightKey, value); if (value) Apply(); else Restore(); }
        }

        public static bool Colour
        {
            get => TankRigMatCap.Colour;
            set => TankRigMatCap.Colour = value;
        }

        public static bool Edges
        {
            get => EditorPrefs.GetBool(EdgesKey, false);
            set => EditorPrefs.SetBool(EdgesKey, value);
        }

        /// <summary>Turns the scene views' lighting off while the tool is active (remembering what it was).</summary>
        public static void Apply()
        {
            if (!Bright) return;
            foreach (SceneView sv in SceneView.sceneViews)
            {
                if (sv == null) continue;
                if (!saved.ContainsKey(sv)) saved[sv] = sv.sceneLighting;
                if (sv.sceneLighting) { sv.sceneLighting = false; sv.Repaint(); }
            }
            TankRigMatCap.Hold(Holder);
        }

        /// <summary>Puts the scene views' lighting and HDRP's MatCap preference back as they were.</summary>
        public static void Restore()
        {
            foreach (var kv in saved)
                if (kv.Key != null && kv.Key.sceneLighting != kv.Value) { kv.Key.sceneLighting = kv.Value; kv.Key.Repaint(); }
            saved.Clear();
            TankRigMatCap.Release(Holder);
        }

        /// <summary>Whether HDRP's MatCap colour preference could be reached (for tests).</summary>
        public static bool CanSetColour => TankRigMatCap.Value != null;
    }

    /// <summary>
    /// HDRP's "MatCap Mode Mix Albedo" preference (an internal class: HDRenderPipelinePreferences.matCapMode.mixAlbedo),
    /// switched while something needs the bright view. Each user holds it by name; the first one remembers the original
    /// value (for the editor session) and the last one to let go puts it back.
    /// </summary>
    public static class TankRigMatCap
    {
        const string ColourKey = "SMCAD.Decor.BrightColour";   // shared with the Tank Rigging copy
        const string OriginalKey = "SMCAD.MatCap.Original";      // shared too: -1 none, 0 / 1 the value before
        static readonly HashSet<string> holders = new HashSet<string>();
        static object mode;            // HDRPs MatCapModeEditorPreferences (one per editor session)
        static FieldInfo mixField;     // its Observable<bool> mixAlbedo: a struct, so it is read and written back whole
        static PropertyInfo valueProp;
        static bool looked;

        /// <summary>Show the material colours in the bright view.</summary>
        public static bool Colour
        {
            get => EditorPrefs.GetBool(ColourKey, false);
            set
            {
                if (value == Colour) return;
                EditorPrefs.SetBool(ColourKey, value);
                if (holders.Count > 0) Value = value;
                SceneView.RepaintAll();
            }
        }

        public static bool Held => holders.Count > 0;

        public static void Hold(string who)
        {
            holders.Add(who);
            var cur = Value;
            if (cur == null) return;
            if (SessionState.GetInt(OriginalKey, -1) < 0) SessionState.SetInt(OriginalKey, cur.Value ? 1 : 0);
            if (cur.Value != Colour) { Value = Colour; SceneView.RepaintAll(); }
        }

        public static void Release(string who)
        {
            if (!holders.Remove(who) || holders.Count > 0) return;
            PutBack();
        }

        static void PutBack()
        {
            int o = SessionState.GetInt(OriginalKey, -1);
            if (o < 0) return;
            SessionState.EraseInt(OriginalKey);
            if (Value != (o == 1)) { Value = o == 1; SceneView.RepaintAll(); }
        }

        // after a script reload nobody holds it any more until the tools draw again: put it back if none comes
        [InitializeOnLoadMethod]
        static void AfterReload()
        {
            double at = EditorApplication.timeSinceStartup + 2.0;
            void Check()
            {
                if (EditorApplication.timeSinceStartup < at) return;
                EditorApplication.update -= Check;
                if (holders.Count == 0) PutBack();
            }
            EditorApplication.update += Check;
        }

        /// <summary>The preference itself (null when this HDRP version does not have it).</summary>
        public static bool? Value
        {
            get
            {
                Find();
                if (mixField == null || valueProp == null) return null;
                try { return valueProp.GetValue(mixField.GetValue(mode)) as bool?; } catch { return null; }
            }
            set
            {
                Find();
                if (value == null || mixField == null || valueProp == null) return;
                try
                {
                    // a boxed copy of the struct: change it, then put it back into the field
                    var box = mixField.GetValue(mode);
                    valueProp.SetValue(box, value.Value);   // also writes HDRP's own EditorPrefs key
                    mixField.SetValue(mode, box);
                }
                catch { }
            }
        }

        static void Find()
        {
            if (looked) return;
            looked = true;
            try
            {
                var t = Type.GetType("UnityEngine.Rendering.HighDefinition.HDRenderPipelinePreferences, Unity.RenderPipelines.HighDefinition.Runtime");
                mode = t?.GetProperty("matCapMode", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null);
                mixField = mode?.GetType().GetField("mixAlbedo", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                valueProp = mixField?.FieldType.GetProperty("value");
            }
            catch { mode = null; mixField = null; valueProp = null; }
        }
    }
}
