using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Builds meshes and prefabs from the CODM files the baker writes.
///
/// CODM exists because Unity has no native glTF importer and the package CDN is
/// unreachable from this machine. The format is a flat binary blob plus a JSON
/// manifest of byte ranges — small enough that the whole reader is below, and
/// it carries what the port needs and glTF would have made awkward: per-part
/// material buckets, the named attachment nodes, and the wear/grime/AO vertex
/// masks that paint bare metal back onto the corners.
/// </summary>
public static class CodmModelBuilder
{
    const string ModelsRoot = "Assets/Art/Models";
    const string MaterialsRoot = "Assets/Art/Materials";
    const string PrefabsRoot = "Assets/Prefabs/Weapons";
    const string WorldPrefabsRoot = "Assets/Prefabs/World";
    const string SoldierPrefabsRoot = "Assets/Prefabs/Soldiers";

    [Serializable]
    public class Manifest
    {
        public string format;
        public string kind;
        public string id;
        public string label;
        public string fxClass;
        public int tris;
        public Part[] parts;
        public Node[] rootNodes;
        public WorldSpecs specs;
        public int binBytes;
    }

    [Serializable]
    public class Part
    {
        public string path;
        public string node;
        public MeshEntry[] meshes;
        public Node[] nodes;
    }

    [Serializable]
    public class MeshEntry
    {
        public string material;
        public int vertexCount;
        public int indexCount;
        public int tris;
        public Range position;
        public Range normal;
        public Range color;
        public Range index;
    }

    [Serializable]
    public class Range
    {
        public int offset;
        public int length;
    }

    [Serializable]
    public class Node
    {
        public string name;
        public float[] position;
        public float[] rotation;
    }

    /// <summary>
    /// The shipped loadout, mirroring `Viewmodel.addWeapon`. Every variant is in
    /// the prefab so a loadout screen can switch them, but only these are on.
    /// </summary>
    static readonly Dictionary<string, string> DefaultVariant = new()
    {
        { "optics", "reddot" },
        { "muzzles", "trilug" },
        { "mags", "std" },
        { "stocks", "standard" },
    };

    [MenuItem("Claude of Duty/Build Weapon Prefabs")]
    public static void BuildModelsMenu() => BuildModels();

    public static int BuildModels()
    {
        int errors = 0;
        int built = 0;
        var manifests = Directory.GetFiles(ModelsRoot, "*.codm.json", SearchOption.AllDirectories);
        foreach (var manifestPath in manifests)
        {
            try
            {
                if (BuildOne(manifestPath)) built++;
            }
            catch (Exception e)
            {
                Debug.LogError($"[port] {manifestPath}: {e}");
                errors++;
            }
        }
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log($"[port] prefabs: {built} built, {errors} errors from {manifests.Length} manifests");
        return errors;
    }

    static bool BuildOne(string manifestPath)
    {
        var unityManifest = manifestPath.Replace('\\', '/');
        var dir = Path.GetDirectoryName(unityManifest).Replace('\\', '/');
        var manifest = JsonUtility.FromJson<Manifest>(File.ReadAllText(manifestPath));
        if (manifest == null || string.IsNullOrEmpty(manifest.id))
        {
            Debug.LogWarning($"[port] unreadable manifest {unityManifest}");
            return false;
        }

        var binPath = $"{dir}/{manifest.id}.codm.bytes";
        if (!File.Exists(binPath))
        {
            Debug.LogError($"[port] {manifest.id}: missing blob {binPath}");
            return false;
        }
        var blob = File.ReadAllBytes(binPath);
        if (blob.Length < manifest.binBytes)
        {
            Debug.LogError($"[port] {manifest.id}: blob is {blob.Length}B, manifest claims {manifest.binBytes}B");
            return false;
        }

        var root = new GameObject(manifest.id);
        bool world = manifest.kind == "world";
        int missingMaterials = 0;
        foreach (var part in manifest.parts)
        {
            var go = new GameObject(part.node);
            go.transform.SetParent(root.transform, false);

            if (part.meshes is { Length: > 0 })
            {
                var mesh = BuildMesh($"{manifest.id}_{part.node}", part.meshes, blob, dir);
                if (mesh == null) continue;
                AssetDatabase.CreateAsset(mesh, $"{dir}/{manifest.id}_{part.node}.asset");

                var filter = go.AddComponent<MeshFilter>();
                filter.sharedMesh = mesh;
                var renderer = go.AddComponent<MeshRenderer>();
                var mats = new Material[part.meshes.Length];
                for (int i = 0; i < part.meshes.Length; i++)
                {
                    mats[i] = FindMaterial(part.meshes[i].material);
                    if (mats[i] == null)
                    {
                        Debug.LogWarning($"[port] {manifest.id}/{part.node}: no material for \"{part.meshes[i].material}\"");
                        missingMaterials++;
                    }
                }
                renderer.sharedMaterials = mats;
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;

                // Level geometry is what the player walks on and shoots at, so
                // it needs collision. A mesh collider per material bucket is
                // coarse — one bucket can be most of the street — but it is
                // exact, which is what matters before anything is optimised.
                if (world)
                {
                    go.isStatic = true;
                    var collider = go.AddComponent<MeshCollider>();
                    collider.sharedMesh = mesh;
                }
            }

            foreach (var n in part.nodes ?? Array.Empty<Node>()) AddNode(go, n);
        }

        foreach (var n in manifest.rootNodes ?? Array.Empty<Node>()) AddNode(root, n);

        if (!world) ApplyDefaultLoadout(root.transform);
        else ApplyWorldExtras(root, manifest);

        var isSoldier = manifest.kind == "soldier";
        var root_ = isSoldier ? SoldierPrefabsRoot : world ? WorldPrefabsRoot : PrefabsRoot;
        PortPipeline.EnsureFolder(root_);
        var prefabPath = $"{root_}/{manifest.id}.prefab";
        PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
        UnityEngine.Object.DestroyImmediate(root);

        Debug.Log($"[port] {manifest.id}: {manifest.tris} tris, {manifest.parts.Length} parts -> {prefabPath}" +
                  (missingMaterials > 0 ? $" ({missingMaterials} missing materials)" : ""));
        return true;
    }

    /// <summary>
    /// Spawn points ride in the world manifest's spec table; they become empties
    /// in the prefab so the game code can find one by tag without a side lookup.
    /// </summary>
    static void ApplyWorldExtras(GameObject root, Manifest manifest)
    {
        var spawns = manifest.specs?.spawnPoints;
        if (spawns == null || spawns.Length == 0) return;
        var group = new GameObject("Spawns");
        group.transform.SetParent(root.transform, false);
        for (int i = 0; i < spawns.Length; i++)
        {
            var s = spawns[i];
            var go = new GameObject($"spawn_{s.tag ?? "any"}_{i}");
            go.transform.SetParent(group.transform, false);
            if (s.position is { Length: >= 3 })
                go.transform.localPosition = new Vector3(s.position[0], s.position[1], s.position[2]);
            go.transform.localRotation = Quaternion.Euler(0f, s.yaw, 0f);
        }
    }

    [Serializable]
    public class WorldSpecs
    {
        public Spawn[] spawnPoints;
    }

    [Serializable]
    public class Spawn
    {
        public float[] position;
        public float yaw;
        public string tag;
    }

    /// <summary>One mesh per part, one submesh per material bucket.</summary>
    static Mesh BuildMesh(string name, MeshEntry[] entries, byte[] blob, string dir)
    {
        int totalVerts = entries.Sum(e => e.vertexCount);
        int totalTris = entries.Sum(e => e.tris);

        var positions = new Vector3[totalVerts];
        var normals = new Vector3[totalVerts];
        var colors = new Color[totalVerts];
        int vBase = 0;

        var indices = new int[entries.Length][];
        for (int e = 0; e < entries.Length; e++)
        {
            var entry = entries[e];

            // Copy each stream straight into a right-sized buffer: the blob is
            // raw little-endian floats, so a scratch array of three would
            // overflow on the first real vertex.
            var pos = new float[entry.vertexCount * 3];
            Buffer.BlockCopy(blob, entry.position.offset, pos, 0, pos.Length * 4);
            var nrm = new float[entry.vertexCount * 3];
            Buffer.BlockCopy(blob, entry.normal.offset, nrm, 0, nrm.Length * 4);
            var col = new float[entry.vertexCount * 3];
            Buffer.BlockCopy(blob, entry.color.offset, col, 0, col.Length * 4);

            for (int i = 0; i < entry.vertexCount; i++)
            {
                positions[vBase + i] = new Vector3(pos[i * 3], pos[i * 3 + 1], pos[i * 3 + 2]);
                normals[vBase + i] = new Vector3(nrm[i * 3], nrm[i * 3 + 1], nrm[i * 3 + 2]);
                colors[vBase + i] = new Color(col[i * 3], col[i * 3 + 1], col[i * 3 + 2], 1f);
            }

            var raw = new int[entry.indexCount];
            Buffer.BlockCopy(blob, entry.index.offset, raw, 0, entry.indexCount * 4);
            if (vBase != 0)
                for (int i = 0; i < raw.Length; i++) raw[i] += vBase;
            indices[e] = raw;
            vBase += entry.vertexCount;
            _ = entry.tris;
        }

        var mesh = new Mesh { name = name };
        if (totalVerts > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        mesh.SetVertices(positions);
        mesh.SetNormals(normals);
        mesh.SetColors(colors);
        mesh.subMeshCount = entries.Length;
        for (int e = 0; e < entries.Length; e++) mesh.SetTriangles(indices[e], e, false);
        mesh.RecalculateBounds();
        _ = totalTris;

        // Weld small numerical drift the procedural builder leaves behind; the
        // geometry arrives non-indexed from single-piece buckets in places.
        MeshUtility.Optimize(mesh);
        return mesh;
    }

    static void AddNode(GameObject parent, Node n)
    {
        var go = new GameObject(n.name);
        go.transform.SetParent(parent.transform, false);
        if (n.position is { Length: >= 3 })
            go.transform.localPosition = new Vector3(n.position[0], n.position[1], n.position[2]);
        if (n.rotation is { Length: >= 4 })
            go.transform.localRotation = new Quaternion(n.rotation[0], n.rotation[1], n.rotation[2], n.rotation[3]);
    }

    static Material FindMaterial(string key)
    {
        var weapon = $"{MaterialsRoot}/weapon/{key}.mat";
        if (File.Exists(weapon)) return AssetDatabase.LoadAssetAtPath<Material>(weapon);
        var library = $"{MaterialsRoot}/library/{key}.mat";
        if (File.Exists(library)) return AssetDatabase.LoadAssetAtPath<Material>(library);
        // The optic bore, lens and cavity blacks are built in code, not baked.
        var special = $"{MaterialsRoot}/special/{key}.mat";
        if (File.Exists(special)) return AssetDatabase.LoadAssetAtPath<Material>(special);
        // World geometry arrives keyed by surface *plus* that building's tint,
        // tile size and wear; those variants are built from the base surface.
        if (key.Contains('|')) return PortPipeline.GetOrCreateVariant(key);
        return null;
    }

    static void ApplyDefaultLoadout(Transform root)
    {
        foreach (var slot in DefaultVariant.Keys)
        {
            var children = new List<Transform>();
            foreach (Transform child in root) if (child.name.StartsWith(slot + "__")) children.Add(child);
            if (children.Count == 0) continue;

            string wanted = DefaultVariant[slot];
            string fallback = slot == "muzzles" ? "brake" : null;
            bool any = children.Any(c => c.name.StartsWith($"{slot}__{wanted}__"));
            if (!any && fallback != null) { wanted = fallback; any = children.Any(c => c.name.StartsWith($"{slot}__{fallback}__")); }
            if (!any) continue;

            foreach (var child in children)
                child.gameObject.SetActive(child.name.StartsWith($"{slot}__{wanted}__"));
        }
    }
}
