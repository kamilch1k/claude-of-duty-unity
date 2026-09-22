using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Rebuilds the baked art into Unity assets.
///
/// The bake (tools/bake in the source repo) writes three things per surface and
/// per weapon: PNG sets with a sidecar of numbers, a CODM mesh blob with a JSON
/// manifest, and a GLB kept for other engines. Unity has no native glTF
/// importer and this machine cannot reach the package CDN, so CODM is what the
/// port imports — it is also the only format that carries the vertex masks.
///
/// Run headless:
///   Unity.exe -batchmode -projectPath &lt;project&gt; -executeMethod PortPipeline.RunAll -logFile &lt;log&gt;
/// </summary>
public static class PortPipeline
{
    const string TexturesRoot = "Assets/Art/Textures";
    const string MaterialsRoot = "Assets/Art/Materials";
    const string ModelsRoot = "Assets/Art/Models";
    const string ShaderPath = "Assets/Shaders/TriplanarLit.shader";

    // ---------------------------------------------------------------- entry --

    [MenuItem("Claude of Duty/Build Materials")]
    public static void BuildMaterialsMenu() => BuildMaterials();

    [MenuItem("Claude of Duty/Build Models")]
    public static void BuildModelsMenu() => CodmModelBuilder.BuildModels();

    [MenuItem("Claude of Duty/Run All")]
    public static void RunAllMenu() => RunAll();

    /// <summary>Batch entry point: materials first, then models that reference them.</summary>
    public static void RunAll()
    {
        int failures = 0;
        failures += BuildMaterials();
        BuildSpecialMaterials();
        failures += CodmModelBuilder.BuildModels();
        Debug.Log(failures == 0
            ? "[port] PIPELINE OK"
            : $"[port] PIPELINE FAILED ({failures} errors)");
        EditorApplication.Exit(failures == 0 ? 0 : 1);
    }

    // ------------------------------------------------------------ materials --

    [Serializable]
    class BakeSidecar
    {
        public string key;
        public string kind;
        public string source;
        public int size;
        public float worldSize;
        public float relief;
        public bool alphaMask;
        public float metresPerTile;
        public float tilesPerMetre;
        public MaterialParams @params;
        public Dictionary<string, BakeFile> files;
    }

    [Serializable]
    class BakeFile
    {
        public string file;
        public int bytes;
    }

    /// <summary>
    /// Only the fields the port uses. Unity's JsonUtility ignores the rest, and
    /// every one of these is present in the sidecars the baker writes.
    /// </summary>
    [Serializable]
    class MaterialParams
    {
        public string uvMode;
        public bool localSpace;
        public bool vertexMasks;
        public float[] weather;
        public float[] macro;
        public float aoStrength = 1f;
        public float scale;
        public string tint;
        public float[] roughness;
        public float normalStrength = 1f;
        public float[] detail;
        public float[] wear;
        public int wearColor;
        public float[] wearMaterial;
        public int grimeColor;
        public float parallax;
        public float[] tilingOffset;
    }

    public static int BuildMaterials()
    {
        var shader = AssetDatabase.LoadAssetAtPath<Shader>(ShaderPath) ?? Shader.Find("ClaudeOfDuty/TriplanarLit");
        if (shader == null)
        {
            Debug.LogError($"[port] shader missing at {ShaderPath}");
            return 1;
        }

        int errors = 0;
        int built = 0;
        foreach (var sidecarPath in Directory.GetFiles(TexturesRoot, "*.bake.json", SearchOption.AllDirectories))
        {
            // Unity paths, not OS paths: the AssetDatabase is the only reliable
            // way to reach importers and asset creation.
            var unityPath = sidecarPath.Replace('\\', '/');
            var text = File.ReadAllText(sidecarPath);
            var sidecar = JsonUtility.FromJson<BakeSidecar>(text);
            if (sidecar == null || string.IsNullOrEmpty(sidecar.key))
            {
                Debug.LogWarning($"[port] unreadable sidecar {unityPath}");
                errors++;
                continue;
            }
            try
            {
                if (BuildOneMaterial(sidecar, unityPath, shader)) built++;
            }
            catch (Exception e)
            {
                Debug.LogError($"[port] {sidecar.key}: {e.Message}");
                errors++;
            }
        }
        AssetDatabase.SaveAssets();
        Debug.Log($"[port] materials: {built} built, {errors} errors");
        return errors;
    }

    static bool BuildOneMaterial(BakeSidecar sidecar, string sidecarPath, Shader shader)
    {
        var dir = Path.GetDirectoryName(sidecarPath).Replace('\\', '/');
        var kind = sidecar.kind == "weapon" ? "weapon" : "library";
        var outDir = $"{MaterialsRoot}/{kind}";
        EnsureFolder(outDir);
        var matPath = $"{outDir}/{sidecar.key}.mat";

        var p = sidecar.@params;
        float tilesPerMetre = sidecar.tilesPerMetre;
        // Weapon surfaces carry their own tile size: the source inverts `scale`
        // into tiles per metre for projected UV modes (materials/index.js).
        if (kind == "weapon" && p != null && p.scale > 0f) tilesPerMetre = 1f / p.scale;

        var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
        bool fresh = mat == null;
        if (fresh) mat = new Material(shader);
        mat.shader = shader;
        mat.name = sidecar.key;

        mat.SetTexture("_Albedo", Configure($"{dir}/{sidecar.key}_albedo.png", TexRole.Albedo));
        mat.SetTexture("_NormalTex", Configure($"{dir}/{sidecar.key}_normal.png", TexRole.Normal));
        mat.SetTexture("_Mask", Configure($"{dir}/{sidecar.key}_mso.png", TexRole.Data));
        var height = Configure($"{dir}/{sidecar.key}_height.png", TexRole.Data);
        if (height != null) mat.SetTexture("_HeightTex", height);

        mat.SetFloat("_Tiling", tilesPerMetre);
        mat.SetFloat("_Ambient", 1f);
        // ENV_OCCLUSION: a shouldered weapon sees about a quarter of the sky.
        mat.SetFloat("_EnvIntensity", kind == "weapon" ? 0.24f : 1f);

        // The viewmodel rig, ported from render/index.js: a 4-light view-space
        // rig plus a hemisphere, applied only to weapon surfaces. A shouldered
        // weapon is lit by its own rig in every shipped FPS, and for good reason
        // — handed one copy of the world sun, a 0.01-albedo receiver goes to a
        // black silhouette whenever the sun is behind it.
        bool weapon = kind == "weapon";
        mat.SetFloat("_ViewRig", weapon ? 1f : 0f);
        mat.SetColor("_RigKey", new Color(1.0000f, 0.8069f, 0.5520f, 2.0f));
        mat.SetColor("_RigFill", new Color(0.3419f, 0.5520f, 1.0000f, 0.6f));
        mat.SetColor("_RigRim", new Color(1.0000f, 0.6795f, 0.3916f, 1.0f));
        mat.SetColor("_RigBounce", new Color(1.0000f, 0.4793f, 0.1946f, 0.5f));
        mat.SetColor("_RigHemi", new Color(0.2747f, 0.4678f, 1.0000f, 0.35f));
        mat.SetColor("_RigHemiGround", new Color(0.0369f, 0.0296f, 0.0231f, 1f));
        mat.SetFloat("_NormalScale", p?.normalStrength ?? 1f);
        mat.SetFloat("_Occlusion", p?.aoStrength ?? 1f);

        if (p?.roughness is { Length: >= 3 })
        {
            mat.SetFloat("_RoughScale", p.roughness[0]);
            mat.SetFloat("_RoughBias", p.roughness[1]);
            mat.SetFloat("_RoughMin", p.roughness[3 <= p.roughness.Length - 1 ? 3 : 2]);
        }

        mat.SetColor("_Tint", ParseColor(p?.tint, Color.white));
        mat.SetColor("_WearColor", ColorFromInt(p?.wearColor ?? 0x6B6E72));
        mat.SetColor("_GrimeColor", ColorFromInt(p?.grimeColor ?? 0x0B0A08));
        mat.SetVector("_WearParams", ToVector4(p?.wear, Vector4.zero));
        mat.SetFloat("_Cavity", p?.weather is { Length: >= 4 } ? p.weather[3] : 0f);

        // Alpha-tested foliage: the albedo's alpha is a cutout, not a height.
        bool alphaTest = sidecar.alphaMask;
        SetKeyword(mat, "_ALPHATEST_ON", alphaTest);
        mat.SetFloat("_Cutoff", 0.5f);
        mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.AlphaTest;

        if (fresh) AssetDatabase.CreateAsset(mat, matPath);
        else EditorUtility.SetDirty(mat);
        return true;
    }

    enum TexRole { Albedo, Normal, Data }

    static readonly Dictionary<string, Texture2D> TextureCache = new();

    /// <summary>
    /// Import settings matter more than usual here: the maps are procedural
    /// noise, so BC compression turns 1-pixel grain into mush, and the shader
    /// depends on the data maps never being sRGB-decoded.
    /// </summary>
    static Texture2D Configure(string path, TexRole role)
    {
        if (TextureCache.TryGetValue(path, out var cached)) return cached;
        if (!File.Exists(path)) return null;

        var importer = AssetImporter.GetAtPath(path) as TextureImporter;
        if (importer != null)
        {
            bool dirty = false;
            var wantType = role == TexRole.Normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
            if (importer.textureType != wantType) { importer.textureType = wantType; dirty = true; }
            bool wantSrgb = role == TexRole.Albedo;
            if (importer.sRGBTexture != wantSrgb) { importer.sRGBTexture = wantSrgb; dirty = true; }
            var wantCompression = role == TexRole.Albedo
                ? TextureImporterCompression.CompressedHQ
                : TextureImporterCompression.Uncompressed;
            if (importer.textureCompression != wantCompression) { importer.textureCompression = wantCompression; dirty = true; }
            if (!importer.mipmapEnabled) { importer.mipmapEnabled = true; dirty = true; }
            if (importer.wrapMode != TextureWrapMode.Repeat) { importer.wrapMode = TextureWrapMode.Repeat; dirty = true; }
            if (importer.anisoLevel != 8) { importer.anisoLevel = 8; dirty = true; }
            if (importer.maxTextureSize < 2048) { importer.maxTextureSize = 2048; dirty = true; }
            if (dirty) importer.SaveAndReimport();
        }

        var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        TextureCache[path] = tex;
        return tex;
    }

    static void SetKeyword(Material m, string keyword, bool on)
    {
        if (on) m.EnableKeyword(keyword);
        else m.DisableKeyword(keyword);
    }

    static Color ParseColor(string hex, Color fallback)
    {
        if (string.IsNullOrEmpty(hex)) return fallback;
        if (!hex.StartsWith("#")) hex = "#" + hex;
        // three stores colours linear after ColorManagement, and the shader
        // multiplies a linear albedo, so hand Unity the linear value too.
        return ColorUtility.TryParseHtmlString(hex, out var c) ? c.linear : fallback;
    }

    static Color ColorFromInt(int value)
    {
        var r = ((value >> 16) & 0xFF) / 255f;
        var g = ((value >> 8) & 0xFF) / 255f;
        var b = (value & 0xFF) / 255f;
        return new Color(r, g, b, 1f).linear;
    }

    static Vector4 ToVector4(float[] a, Vector4 fallback)
    {
        if (a == null || a.Length < 4) return fallback;
        return new Vector4(a[0], a[1], a[2], a[3]);
    }

    internal     static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        var parent = Path.GetDirectoryName(path).Replace('\\', '/');
        var leaf = Path.GetFileName(path);
        if (!AssetDatabase.IsValidFolder(parent)) EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, leaf);
    }

    // ------------------------------------------------------ special mats --

    /// <summary>
    /// The five materials the weapon system builds in code rather than from a
    /// texture set: the optic's light-trap bore, its lens and bezel, the vignette
    /// behind the reticle, and the cavity black. Values are lifted from
    /// `WeaponMaterials` in src/weapons/materials.js, since these are flat-shaded
    /// there too — running them through the triplanar shader would grain them.
    /// </summary>
    public static void BuildSpecialMaterials()
    {
        var lit = Shader.Find("Universal Render Pipeline/Lit");
        if (lit == null)
        {
            Debug.LogWarning("[port] URP/Lit not found; special materials skipped");
            return;
        }
        EnsureFolder($"{MaterialsRoot}/special");

        Opaque(lit, "cavity", new Color(0x0a / 255f, 0x0c / 255f, 0x0e / 255f), 0f, 0f);
        Opaque(lit, "optic_tube", new Color(0x1d / 255f, 0x20 / 255f, 0x23 / 255f), 0f, 0.1f);
        Opaque(lit, "lens_ring", new Color(0x05 / 255f, 0x07 / 255f, 0x0a / 255f), 0.8f, 0.5f);
        Transparent(lit, "glass", new Color(0x12 / 255f, 0x1c / 255f, 0x22 / 255f), 0.1f, 0.97f);
        Transparent(lit, "lens_vig", new Color(0x14 / 255f, 0x06 / 255f, 0x0a / 255f), 0.55f, 0.2f);
        AssetDatabase.SaveAssets();
    }

    static Material Opaque(Shader shader, string key, Color colour, float metallic, float smoothness)
    {
        var path = $"{MaterialsRoot}/special/{key}.mat";
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat == null)
        {
            mat = new Material(shader);
            AssetDatabase.CreateAsset(mat, path);
        }
        mat.SetColor("_BaseColor", colour.linear);
        mat.SetFloat("_Metallic", metallic);
        mat.SetFloat("_Smoothness", smoothness);
        EditorUtility.SetDirty(mat);
        return mat;
    }

    static Material Transparent(Shader shader, string key, Color colour, float alpha, float smoothness)
    {
        var mat = Opaque(shader, key, colour, 0f, smoothness);
        var tinted = new Color(colour.r, colour.g, colour.b, alpha);
        mat.SetColor("_BaseColor", tinted.linear);
        mat.SetFloat("_Surface", 1f);
        mat.SetFloat("_Blend", 0f);
        mat.SetFloat("_ZWrite", 0f);
        mat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
        mat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        mat.SetOverrideTag("RenderType", "Transparent");
        mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        EditorUtility.SetDirty(mat);
        return mat;
    }
}
