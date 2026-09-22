using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Renders the built prefabs in Unity and writes PNGs beside the baker's own
/// previews.
///
/// The out-of-engine preview proves the export survived; this one proves the
/// *import* did — materials found, alpha not inverted, projection scale right,
/// the weapon facing the way the port expects. It is the only check that
/// exercises the shader, the import settings and the prefab wiring together.
/// </summary>
public static class PortPreview
{
    const string PrefabsRoot = "Assets/Prefabs/Weapons";

    static readonly (string name, float yaw, float pitch)[] Shots =
    {
        ("side", 100f, 6f),
        ("quarter", 38f, 20f),
    };

    [MenuItem("Claude of Duty/Render Previews")]
    public static void RenderPreviewsMenu() => RenderPreviews();

    public static void RenderPreviews()
    {
        // Rebuild first so a preview never shows a stale material.
        PortPipeline.BuildMaterials();
        PortPipeline.BuildSpecialMaterials();
        CodmModelBuilder.BuildModels();

        var outDir = ResolveOutDir();
        Directory.CreateDirectory(outDir);
        var prefabs = Directory.GetFiles(PrefabsRoot, "*.prefab");
        if (prefabs.Length == 0)
        {
            Debug.LogError("[port] no prefabs to render");
            EditorApplication.Exit(1);
            return;
        }

        foreach (var path in prefabs) Render(path, outDir);
        Debug.Log($"[port] previews written to {outDir}");
    }

    static string ResolveOutDir()
    {
        // <project>/Assets -> <project> -> workspace -> the source repo's bake out.
        var project = Directory.GetParent(Application.dataPath)!.FullName;
        var workspace = Directory.GetParent(project)!.FullName;
        return Path.Combine(workspace, "claude-of-duty-optimized", "tools", "bake", "out", "unity");
    }

    static void AddLight(string name, float intensity, float pitch, float yaw)
    {
        var light = new GameObject(name).AddComponent<Light>();
        light.type = LightType.Directional;
        light.intensity = intensity;
        light.transform.rotation = Quaternion.Euler(pitch, yaw, 0f);
    }

    static void Render(string prefabPath, string outDir)
    {
        var id = Path.GetFileNameWithoutExtension(prefabPath);
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath.Replace('\\', '/'));
        var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);

        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.40f, 0.43f, 0.48f);
        RenderSettings.fog = false;

        // The environment specular term needs something to reflect; in a game
        // scene that is the sky. Without a skybox the shader's env sample is
        // black and the weapon reads as unlit.
        var skyShader = Shader.Find("Skybox/Procedural");
        if (skyShader != null)
        {
            var sky = new Material(skyShader);
            sky.SetFloat("_SunSize", 0.04f);
            sky.SetFloat("_AtmosphereThickness", 0.7f);
            sky.SetColor("_SkyTint", new Color(0.42f, 0.52f, 0.68f));
            sky.SetColor("_GroundColor", new Color(0.22f, 0.20f, 0.18f));
            // A daylight sky is orders of magnitude brighter than a preview
            // skybox at exposure 1. The browser reflects a PMREM of a real
            // atmosphere, and the weapon's broad specular comes from it — at 1.1
            // the gun reads as a silhouette for want of environment radiance, not
            // for want of albedo.
            sky.SetFloat("_Exposure", 5.5f);
            RenderSettings.skybox = sky;
            DynamicGI.UpdateEnvironment();
        }

        // One soft key for the *world*, nothing more: the weapon's own rig is in
        // the shader (_ViewRig), so this render is lit the way the game will be
        // rather than by a preview-only light rig that could hide a real problem.
        AddLight("key", 2.4f, 38f, 146f);
        AddLight("fill", 0.7f, 12f, -40f);

        var bounds = new Bounds();
        bool any = false;
        foreach (var r in instance.GetComponentsInChildren<Renderer>())
        {
            if (!r.gameObject.activeInHierarchy) continue;
            if (!any) { bounds = r.bounds; any = true; }
            else bounds.Encapsulate(r.bounds);
        }
        if (!any)
        {
            Debug.LogWarning($"[port] {id}: nothing enabled to render");
            return;
        }

        var camGo = new GameObject("cam");
        var cam = camGo.AddComponent<Camera>();
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.075f, 0.085f, 0.10f);
        cam.fieldOfView = 34f;
        cam.nearClipPlane = 0.01f;
        cam.farClipPlane = 100f;

        var rt = new RenderTexture(1200, 800, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
        cam.targetTexture = rt;

        var radius = bounds.size.magnitude * 0.5f;
        var dist = radius / Mathf.Tan(Mathf.Deg2Rad * 17f) * 1.05f;

        foreach (var (name, yaw, pitch) in Shots)
        {
            var dir = Quaternion.Euler(-pitch, yaw, 0f) * Vector3.forward;
            camGo.transform.position = bounds.center - dir * dist;
            camGo.transform.rotation = Quaternion.LookRotation(dir, Vector3.up);

            cam.Render();
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            File.WriteAllBytes(Path.Combine(outDir, $"{id}_{name}.png"), tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex);
        }

        cam.targetTexture = null;
        rt.Release();
        UnityEngine.Object.DestroyImmediate(rt);
        UnityEngine.Object.DestroyImmediate(instance);
        _ = scene;
    }
}
