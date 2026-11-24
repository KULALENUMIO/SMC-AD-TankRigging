using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace TankRig.Editor
{
    /// <summary>Brings the track FBX files (exported from Blender) into the project.</summary>
    public static class TankRigAssets
    {
        public const string TrackFolder = "Assets/TankRig/Tracks";
        public const string TextureFolder = TrackFolder + "/Textures";

        /// <summary>
        /// The track FBX files shipped with the package ("Tracks~" inside it: Unity does not import folders ending in ~, so they
        /// are copied into the project instead — works for git / read-only installs too).
        /// </summary>
        public static string DefaultSourceFolder()
        {
            var pi = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(TankRigAssets).Assembly);
            if (pi == null || string.IsNullOrEmpty(pi.resolvedPath)) return "";
            return Path.GetFullPath(Path.Combine(pi.resolvedPath, "Tracks~"));
        }

        /// <summary>Copies the bundled track models into the project the first time they are needed.</summary>
        public static bool EnsureDefaultTracks()
        {
            if (LoadTrack("TankTrack_6W") && LoadTrack("TankTrack_Link")) return true;
            return ImportTracks(DefaultSourceFolder()) > 0;
        }

        /// <summary>Copies every FBX of <paramref name="sourceFolder"/> into Assets/TankRig/Tracks and prepares them.</summary>
        public static int ImportTracks(string sourceFolder)
        {
            if (string.IsNullOrEmpty(sourceFolder) || !Directory.Exists(sourceFolder)) return 0;
            Directory.CreateDirectory(TrackFolder);
            var copied = new List<string>();
            foreach (var file in Directory.GetFiles(sourceFolder, "*.fbx"))
            {
                string dst = TrackFolder + "/" + Path.GetFileName(file);
                File.Copy(file, dst, true);
                copied.Add(dst);
            }
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            foreach (var p in copied) PrepareTrackModel(p);
            return copied.Count;
        }

        /// <summary>
        /// Extracts the textures embedded in the FBX so its materials get them. Colour palettes (flat swatches looked up by UV)
        /// are sampled without filtering or mipmaps so neighbouring swatches never bleed into each other.
        /// </summary>
        public static void PrepareTrackModel(string assetPath)
        {
            if (!(AssetImporter.GetAtPath(assetPath) is ModelImporter mi)) return;
            Directory.CreateDirectory(TextureFolder);
            mi.ExtractTextures(TextureFolder);
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            foreach (var guid in AssetDatabase.FindAssets("t:Texture2D", new[] { TextureFolder }))
            {
                string tp = AssetDatabase.GUIDToAssetPath(guid);
                if (!(AssetImporter.GetAtPath(tp) is TextureImporter ti)) continue;
                if (!Path.GetFileNameWithoutExtension(tp).ToLowerInvariant().Contains("palette")) continue;
                if (ti.filterMode == FilterMode.Point && !ti.mipmapEnabled) continue;
                ti.filterMode = FilterMode.Point;
                ti.mipmapEnabled = false;
                ti.textureCompression = TextureImporterCompression.Uncompressed;
                ti.SaveAndReimport();
            }
            mi.SaveAndReimport();
        }

        public static GameObject LoadTrack(string fileName) =>
            AssetDatabase.LoadAssetAtPath<GameObject>(TrackFolder + "/" + fileName + ".fbx");

        /// <summary>Track link designs that come with the plugin (same pitch 0.25 / width 0.5 / pin and ground positions).</summary>
        public static readonly (string file, string label)[] BuiltInLinks =
        {
            ("TankTrack_Link", "標準"),
            ("TankTrack_Link_Flat", "平板"),
            ("TankTrack_Link_Chevron", "V字"),
            ("TankTrack_Link_RubberPad", "ゴムパッド"),
            ("TankTrack_Link_Skeleton", "スケルトン"),
        };

        /// <summary>Every link model in the track folder: the built-in designs first, then any other "TankTrack_Link*" FBX.</summary>
        public static List<(GameObject asset, string label)> LinkDesigns()
        {
            var list = new List<(GameObject, string)>();
            var known = new HashSet<string>();
            foreach (var (file, label) in BuiltInLinks)
            {
                known.Add(file);
                var a = LoadTrack(file);
                if (a) list.Add((a, label));
            }
            if (Directory.Exists(TrackFolder))
                foreach (var f in Directory.GetFiles(TrackFolder, "TankTrack_Link*.fbx"))
                {
                    string n = Path.GetFileNameWithoutExtension(f);
                    if (known.Contains(n)) continue;
                    var a = LoadTrack(n);
                    if (a) list.Add((a, n.Replace("TankTrack_Link_", "")));
                }
            return list;
        }

        /// <summary>Distance from the link's pin axis down to its ground face (local up = +Y after import).</summary>
        public static float LinkGroundDepth(GameObject linkAsset, float fallback)
        {
            var mf = linkAsset ? linkAsset.GetComponentInChildren<MeshFilter>() : null;
            if (!mf || !mf.sharedMesh) return fallback;
            return Mathf.Max(0.01f, -mf.sharedMesh.bounds.min.y * mf.transform.localScale.y);
        }
    }

    /// <summary>
    /// Track models: matte wheels and links (the FBX carries no roughness). Runs after the render pipeline's own material setup.
    /// </summary>
    class TankTrackModelPostprocessor : AssetPostprocessor
    {
        public override int GetPostprocessOrder() => 10000;

        void OnPostprocessMaterial(Material m)
        {
            if (!assetPath.Replace('\\', '/').StartsWith(TankRigAssets.TrackFolder + "/")) return;
            if (m.HasProperty("_Smoothness"))
                m.SetFloat("_Smoothness", m.name.Contains("Pin") ? 0.45f : m.name.Contains("Rubber") ? 0.06f : m.name.Contains("Cast") ? 0.25f : 0.18f);
        }
    }
}
