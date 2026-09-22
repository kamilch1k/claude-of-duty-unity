using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

/// <summary>
/// Builds a test range and renders a first-person frame from it.
///
/// The point is not the range — it is that the player, the weapon in hand and
/// the baked world materials are proven to work together in one scene, in
/// Unity, before any of the real level is ported. A bake that imports cleanly
/// can still be useless in first person: wrong scale, wrong facing, a weapon
/// that clips the near plane.
///
///   Unity.exe -batchmode -projectPath . -executeMethod TestSceneBuilder.BuildAndRender -logFile &lt;log&gt;
/// </summary>
public static class TestSceneBuilder
{
    const string ScenePath = "Assets/Scenes/TestRange.unity";
    const string MaterialsRoot = "Assets/Art/Materials";
    const string WeaponPrefab = "Assets/Prefabs/Weapons/rifle.prefab";

    /// <summary>
    /// The rifle's hip pose, from WEAPON_DEFS in src/weapons/defs.js:
    ///   hipPos [0.118, -0.185, -0.255]  hipRot [-0.05, 0.081, -0.135]
    ///
    /// Ported across the handedness flip between the two cameras: three's camera
    /// space looks down -Z and Unity's down +Z, so the position's Z is negated
    /// and, because flipping an axis conjugates rotations, X and Y rotations
    /// negate with it while Z does not. (Unity composes Euler angles in a
    /// different order to three, which for angles this small — the largest is
    /// 7.7 degrees — differs by well under a tenth of a degree.)
    /// </summary>
    static readonly Vector3 WeaponHipPos = new(0.118f, -0.185f, 0.255f);
    static readonly Vector3 WeaponHipRotDeg = new(2.865f, -4.641f, -7.735f);

    /// <summary>config.fov is 70 vertical; the viewmodel pass runs at viewFov 0.86 of it.</summary>
    const float WorldFov = 70f;
    const float ViewmodelFov = WorldFov * 0.86f;

    /// <summary>The weapon renders on its own layer so only the viewmodel camera sees it.</summary>
    const int ViewmodelLayer = 8;

    [MenuItem("Claude of Duty/Build Test Range")]
    public static void BuildMenu() => Build();

    [MenuItem("Claude of Duty/Build Test Range and Render")]
    public static void BuildAndRenderMenu() => BuildAndRender();

    public static void BuildAndRender()
    {
        BuildStreet();
        VerifyFiring();
        RenderFirstPerson();
        EditorApplication.Exit(0);
    }

    /// <summary>
    /// The playable scene: the baked street, the player on a spawn point, the
    /// rifle in hand, targets to shoot, and the two-camera viewmodel stack.
    /// </summary>
    [MenuItem("Claude of Duty/Build Street Scene")]
    public static void BuildStreet()
    {
        PortPipeline.BuildMaterials();
        PortPipeline.BuildSpecialMaterials();

        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Skybox;
        RenderSettings.ambientIntensity = 1f;

        var skyShader = Shader.Find("Skybox/Procedural");
        if (skyShader != null)
        {
            var sky = new Material(skyShader);
            sky.SetFloat("_SunSize", 0.04f);
            sky.SetFloat("_AtmosphereThickness", 0.7f);
            sky.SetFloat("_Exposure", 5.5f);
            RenderSettings.skybox = sky;
        }
        DynamicGI.UpdateEnvironment();

        var sun = new GameObject("Sun").AddComponent<Light>();
        sun.type = LightType.Directional;
        sun.intensity = 2.4f;
        sun.color = new Color(1f, 0.91f, 0.77f);
        sun.transform.rotation = Quaternion.Euler(42f, 146f, 0f);
        sun.shadows = LightShadows.Soft;

        var worldPrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/World/street.prefab");
        if (worldPrefab == null)
        {
            Debug.LogError("[port] no street prefab — run the world bake and PortPipeline first");
            return;
        }
        var world = (GameObject)PrefabUtility.InstantiatePrefab(worldPrefab);

        // Stand where the level says a player stands.
        var spawn = world.transform.Find("Spawns")?.GetComponentsInChildren<Transform>()
            .FirstOrDefault(t => t.name.StartsWith("spawn_"));
        var spawnPos = spawn ? spawn.position : new Vector3(0f, 0.5f, 0f);
        var spawnYaw = spawn ? spawn.eulerAngles.y : 0f;

        var player = new GameObject("Player");
        player.transform.position = spawnPos + Vector3.up * 0.2f;
        player.transform.rotation = Quaternion.Euler(0f, spawnYaw, 0f);
        var cc = player.AddComponent<CharacterController>();
        cc.height = PlayerTuning.PlayerHeight;
        cc.radius = 0.34f;
        cc.center = new Vector3(0f, PlayerTuning.PlayerHeight * 0.5f, 0f);
        cc.slopeLimit = 50f;
        cc.stepOffset = 0.42f;

        // head (aim) -> pivot (recoil) -> camera + weapon
        var head = new GameObject("Head");
        head.transform.SetParent(player.transform, false);
        head.transform.localPosition = new Vector3(0f, PlayerTuning.Stand.Eye, 0f);

        var pivot = new GameObject("RecoilPivot");
        pivot.transform.SetParent(head.transform, false);

        var cam = head.AddComponent<Camera>();
        cam.fieldOfView = WorldFov;
        cam.nearClipPlane = 0.01f;
        cam.farClipPlane = 500f;
        cam.tag = "MainCamera";
        cam.cullingMask = ~(1 << ViewmodelLayer);
        head.AddComponent<AudioListener>();

        var baseData = cam.GetUniversalAdditionalCameraData();
        baseData.renderType = CameraRenderType.Base;

        // The viewmodel pass, done the way URP wants it: an overlay camera on the
        // base camera's stack. Two manual Camera.Render calls into one target do
        // not composite — the second pass takes the colour buffer with it.
        var vmGo = new GameObject("ViewmodelCamera");
        vmGo.transform.SetParent(pivot.transform, false);
        var vmCam = vmGo.AddComponent<Camera>();
        vmCam.fieldOfView = ViewmodelFov;
        vmCam.nearClipPlane = 0.01f;
        vmCam.farClipPlane = 12f;
        vmCam.clearFlags = CameraClearFlags.Depth;
        vmCam.cullingMask = 1 << ViewmodelLayer;
        var vmData = vmCam.GetUniversalAdditionalCameraData();
        vmData.renderType = CameraRenderType.Overlay;
        baseData.cameraStack.Add(vmCam);

        var motor = player.AddComponent<PlayerMotor>();
        motor.head = head.transform;

        var weaponSystem = player.AddComponent<WeaponSystem>();
        weaponSystem.viewCamera = cam;
        weaponSystem.recoilPivot = pivot.transform;

        var weaponPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(WeaponPrefab);
        if (weaponPrefab != null)
        {
            var weapon = (GameObject)PrefabUtility.InstantiatePrefab(weaponPrefab);
            weapon.transform.SetParent(pivot.transform, false);
            weapon.transform.localPosition = WeaponHipPos;
            weapon.transform.localRotation = Quaternion.Euler(WeaponHipRotDeg);
            SetLayerRecursive(weapon, ViewmodelLayer);
            foreach (var r in weapon.GetComponentsInChildren<Renderer>())
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            var muzzleNode = weapon.transform.Find("node_muzzle");
            if (muzzleNode) weaponSystem.SetMuzzle(muzzleNode);
        }

        // A muzzle flash that exists so the shot is visible; the real one is a
        // particle system upstream.
        var flash = new GameObject("MuzzleFlash").AddComponent<Light>();
        flash.transform.SetParent(weaponSystem.muzzle ? weaponSystem.muzzle : pivot.transform, false);
        flash.type = LightType.Point;
        flash.range = 6f;
        flash.color = new Color(1f, 0.85f, 0.6f);
        flash.enabled = false;
        weaponSystem.muzzleFlash = flash;

        // Targets, placed in front of the spawn and snapped to the ground.
        var targetMat = AssetDatabase.LoadAssetAtPath<Material>("Assets/Art/Materials/library/fabric.mat");
        for (int i = 0; i < 6; i++)
        {
            float z = 8f + i * 4f;
            float x = (i % 3 - 1) * 3.5f;
            var pos = spawnPos + Quaternion.Euler(0f, spawnYaw, 0f) * new Vector3(x, 0f, z);
            if (Physics.Raycast(pos + Vector3.up * 6f, Vector3.down, out var ground, 40f))
                pos = ground.point;
            var t = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            t.name = $"Target{i}";
            t.transform.position = pos + Vector3.up * 0.95f;
            t.transform.localScale = new Vector3(0.55f, 0.95f, 0.55f);
            if (targetMat) t.GetComponent<MeshRenderer>().sharedMaterial = targetMat;
            t.AddComponent<Target>();
        }

        // Enemies: one per spawn point, so they start where the level intends.
        var enemies = new System.Collections.Generic.List<Enemy>();
        var soldierPaths = new[]
        {
            "Assets/Prefabs/Soldiers/vanguard.prefab",
            "Assets/Prefabs/Soldiers/irregular.prefab",
            "Assets/Prefabs/Soldiers/breacher.prefab",
        };
        var allSpawns = world.transform.Find("Spawns")?.GetComponentsInChildren<Transform>() ?? new Transform[0];
        int index = 0;
        foreach (var node in allSpawns)
        {
            if (!node.name.StartsWith("spawn_")) continue;
            var prefabPath = soldierPaths[index % soldierPaths.Length];
            var soldierPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (soldierPrefab == null) { Debug.LogWarning($"[port] missing {prefabPath}"); break; }

            // Keep the player's own spawn for the player.
            if (Vector3.Distance(node.position, spawnPos) < 1.5f) continue;

            var soldier = (GameObject)PrefabUtility.InstantiatePrefab(soldierPrefab);
            soldier.name = $"Enemy{index}";
            soldier.transform.position = node.position;
            soldier.transform.rotation = Quaternion.Euler(0f, node.eulerAngles.y + 180f, 0f);

            var ccE = soldier.AddComponent<CharacterController>();
            ccE.height = PlayerTuning.PlayerHeight;
            ccE.radius = 0.34f;
            ccE.center = new Vector3(0f, PlayerTuning.PlayerHeight * 0.5f, 0f);
            ccE.stepOffset = 0.42f;

            var eye = new GameObject("Eye");
            eye.transform.SetParent(soldier.transform, false);
            eye.transform.localPosition = new Vector3(0f, 1.66f, 0f);
            var muzzle = new GameObject("Muzzle");
            muzzle.transform.SetParent(soldier.transform, false);
            muzzle.transform.localPosition = new Vector3(0.12f, 1.35f, 0.35f);

            var flashE = new GameObject("EnemyFlash").AddComponent<Light>();
            flashE.transform.SetParent(muzzle.transform, false);
            flashE.type = LightType.Point;
            flashE.range = 5f;
            flashE.enabled = false;

            var enemy = soldier.AddComponent<Enemy>();
            enemy.eye = eye.transform;
            enemy.muzzle = muzzle.transform;
            enemy.muzzleFlash = flashE;
            enemy.fireClip = LoadClip("weapon_rifle_3p");
            enemy.deathClip = LoadClip("bodyfall");
            enemies.Add(enemy);
            index++;
        }

        var health = player.AddComponent<PlayerHealth>();
        health.hurtClip = LoadClip("impact_flesh");
        health.deathClip = LoadClip("ui_lowhealth");

        weaponSystem.fireClip = LoadClip("weapon_rifle_1p");
        weaponSystem.reloadClip = LoadClip("reload_start");
        weaponSystem.hitClip = LoadClip("ui_hitmarker");

        var hud = player.AddComponent<Hud>();
        hud.weapon = weaponSystem;
        hud.health = health;
        hud.enemies = enemies.ToArray();
        weaponSystem.hud = hud;

        Physics.SyncTransforms();
        Directory.CreateDirectory("Assets/Scenes");
        EditorSceneManager.SaveScene(scene, StreetScenePath);
        Debug.Log($"[port] street scene saved to {StreetScenePath} (spawn {spawnPos}, yaw {spawnYaw:0.0}, {enemies.Count} enemies)");
    }

    static AudioClip LoadClip(string name)
    {
        var path = $"Assets/Art/Audio/{name}.wav";
        var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
        if (clip == null) Debug.LogWarning($"[port] no audio clip at {path}");
        return clip;
    }

    const string StreetScenePath = "Assets/Scenes/Street.unity";

    /// <summary>
    /// Fire a burst without entering play mode and report what it hit.
    ///
    /// A scene that loads is not the same as a gun that works: this proves the
    /// level has collision, that the ray reaches it, that a target takes damage
    /// and dies, and that the recoil spring moves the pivot the way it should.
    /// </summary>
    public static void VerifyFiring()
    {
        var player = GameObject.Find("Player");
        var weapon = player ? player.GetComponent<WeaponSystem>() : null;
        var motor = player ? player.GetComponent<PlayerMotor>() : null;
        var pivot = GameObject.Find("Player/Head/RecoilPivot");
        if (weapon == null)
        {
            Debug.LogError("[port] no weapon system to verify");
            return;
        }

        var cam = weapon.viewCamera;
        if (cam)
        {
            bool hitSomething = Physics.Raycast(cam.transform.position, cam.transform.forward, out var probe, 300f);
            Debug.Log($"[port] aim probe from {cam.transform.position}: " +
                      (hitSomething ? $"hit {probe.collider.name} at {probe.point} ({probe.distance:0.0}m)" : "nothing within 300m"));
        }

        weapon.Prime();
        int fired = 0;
        int hitsBefore = weapon.Hits;
        // Synthetic clock: 12 rounds a tenth of a second apart, so the rate of
        // fire is exercised rather than bypassed.
        for (int i = 0; i < 12; i++) if (weapon.Fire(1f + i * 0.1f)) fired++;
        float pitch = pivot ? pivot.transform.localEulerAngles.x : 0f;
        var dead = Object.FindObjectsByType<Target>(FindObjectsSortMode.None).Count(t => t.Dead);
        Debug.Log($"[port] burst: fired={fired} ammo={weapon.Ammo}/{weapon.magSize} hits={weapon.Hits - hitsBefore} " +
                  $"recoilPivotPitch={pitch:0.00} targetsDead={dead}");
        _ = hitsBefore;
    }

    public static void Build()
    {
        // Materials first: the range is the first place a material change is
        // visible, and a stale material would make the range lie about it.
        PortPipeline.BuildMaterials();
        PortPipeline.BuildSpecialMaterials();

        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        // Sky and sun: the world materials are triplanar tiles lit by the scene,
        // so without an environment the range reads as a black room.
        var skyShader = Shader.Find("Skybox/Procedural");
        if (skyShader != null)
        {
            var sky = new Material(skyShader);
            sky.SetFloat("_SunSize", 0.04f);
            sky.SetFloat("_AtmosphereThickness", 0.7f);
            sky.SetFloat("_Exposure", 5.5f);
            RenderSettings.skybox = sky;
        }
        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Skybox;
        RenderSettings.ambientIntensity = 1f;
        DynamicGI.UpdateEnvironment();

        var sun = new GameObject("Sun").AddComponent<Light>();
        sun.type = LightType.Directional;
        sun.intensity = 2.4f;
        sun.color = new Color(1f, 0.91f, 0.77f);
        sun.transform.rotation = Quaternion.Euler(42f, 146f, 0f);
        sun.shadows = LightShadows.Soft;

        // Floor and a few walls, using the baked surfaces so the projection
        // scale is visible: concrete_floor tiles at 2.5 m, brick at 1.35 m.
        Slab("Floor", new Vector3(0f, -0.5f, 0f), new Vector3(40f, 1f, 40f), "concrete_floor");
        Slab("WallA", new Vector3(0f, 2f, 12f), new Vector3(24f, 5f, 0.4f), "brick");
        Slab("WallB", new Vector3(-12f, 2f, 0f), new Vector3(0.4f, 5f, 24f), "concrete");
        Slab("Crate", new Vector3(3f, 0.35f, 4f), new Vector3(0.7f, 0.7f, 0.7f), "wood");
        Slab("Barrier", new Vector3(-3f, 0.5f, 6f), new Vector3(3f, 1f, 0.3f), "metal_painted");

        var player = new GameObject("Player");
        player.transform.position = new Vector3(0f, 0.05f, -4f);
        var cc = player.AddComponent<CharacterController>();
        cc.height = PlayerTuning.PlayerHeight;
        cc.radius = 0.34f;
        cc.center = new Vector3(0f, PlayerTuning.PlayerHeight * 0.5f, 0f);
        cc.slopeLimit = 50f;
        cc.stepOffset = 0.42f;

        var head = new GameObject("Head");
        head.transform.SetParent(player.transform, false);
        head.transform.localPosition = new Vector3(0f, PlayerTuning.Stand.Eye, 0f);
        var cam = head.AddComponent<Camera>();
        cam.fieldOfView = WorldFov;
        cam.nearClipPlane = 0.01f;
        cam.farClipPlane = 400f;
        cam.tag = "MainCamera";
        cam.cullingMask = ~(1 << ViewmodelLayer);
        head.AddComponent<AudioListener>();

        // The viewmodel pass: a second camera with the weapon's own FOV, drawing
        // only the weapon, over the world's depth. Upstream renders it into its
        // own MSAA target for the same reason — the weapon must not clip into a
        // wall it is standing next to, and its FOV is not the world's.
        var vmGo = new GameObject("ViewmodelCamera");
        vmGo.transform.SetParent(head.transform, false);
        var vmCam = vmGo.AddComponent<Camera>();
        vmCam.fieldOfView = ViewmodelFov;
        vmCam.nearClipPlane = 0.01f;
        vmCam.farClipPlane = 12f;
        vmCam.clearFlags = CameraClearFlags.Depth;
        vmCam.cullingMask = 1 << ViewmodelLayer;
        vmCam.depth = 1;

        var motor = player.AddComponent<PlayerMotor>();
        motor.head = head.transform;

        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(WeaponPrefab);
        if (prefab != null)
        {
            var weapon = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            weapon.transform.SetParent(head.transform, false);
            weapon.transform.localPosition = WeaponHipPos;
            weapon.transform.localRotation = Quaternion.Euler(WeaponHipRotDeg);
            SetLayerRecursive(weapon, ViewmodelLayer);
            // The rig is in the shader, so the weapon needs no lights of its own
            // — but it must not cast shadows into the world it is not part of.
            foreach (var r in weapon.GetComponentsInChildren<Renderer>())
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }
        else
        {
            Debug.LogWarning($"[port] no weapon prefab at {WeaponPrefab}");
        }

        Directory.CreateDirectory("Assets/Scenes");
        EditorSceneManager.SaveScene(scene, ScenePath);
        Debug.Log($"[port] test range saved to {ScenePath}");
    }

    static void Slab(string name, Vector3 centre, Vector3 size, string materialKey)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        go.transform.position = centre;
        go.transform.localScale = size;
        var path = $"{MaterialsRoot}/library/{materialKey}.mat";
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat != null) go.GetComponent<MeshRenderer>().sharedMaterial = mat;
        else Debug.LogWarning($"[port] {name}: no material at {path}");
        // Cube UVs are meaningless to a triplanar shader, but the primitive's
        // scale is what the projection reads, so nothing else is needed.
    }

    static void SetLayerRecursive(GameObject go, int layer)
    {
        go.layer = layer;
        foreach (Transform child in go.transform) SetLayerRecursive(child.gameObject, layer);
    }

    /// <summary>Render what the player sees: world pass, then the viewmodel pass over it.</summary>
    static void RenderFirstPerson()
    {
        var head = GameObject.Find("Player/Head");
        var cam = head ? head.GetComponent<Camera>() : null;
        var vmCam = head ? head.transform.Find("ViewmodelCamera")?.GetComponent<Camera>() : null;
        if (cam == null)
        {
            Debug.LogError("[port] no player camera to render from");
            return;
        }

        var outDir = Path.Combine(
            Directory.GetParent(Directory.GetParent(Application.dataPath)!.FullName)!.FullName,
            "claude-of-duty-optimized", "tools", "bake", "out", "unity");
        Directory.CreateDirectory(outDir);

        var rt = new RenderTexture(1280, 720, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
        cam.targetTexture = rt;
        cam.Render();
        Write(rt, Path.Combine(outDir, "scene_worldonly.png"));

        // Say out loud what the camera can see, so a blank frame is a diagnosis
        // rather than a mystery.
        int enabled = 0;
        int visible = 0;
        var lines = new System.Text.StringBuilder();
        foreach (var r in Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None))
        {
            enabled++;
            if (r.isVisible) visible++;
            if (enabled <= 8)
                lines.Append($"\n    {r.name} layer={r.gameObject.layer} visible={r.isVisible} mat={(r.sharedMaterial ? r.sharedMaterial.name : "none")}");
        }
        Debug.Log($"[port] camera pos={cam.transform.position} euler={cam.transform.eulerAngles} fov={cam.fieldOfView} mask={cam.cullingMask} far={cam.farClipPlane}" +
                  $"\n  renderers={enabled} visible={visible}{lines}");

        // The viewmodel camera clears depth only, so this composites the weapon
        // over the world exactly the way the game's second pass does.
        if (vmCam)
        {
            vmCam.targetTexture = rt;
            vmCam.Render();
        }

        var prev = RenderTexture.active;
        RenderTexture.active = rt;
        var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
        tex.Apply();
        RenderTexture.active = prev;
        File.WriteAllBytes(Path.Combine(outDir, "scene_firstperson.png"), tex.EncodeToPNG());
        Object.DestroyImmediate(tex);

        cam.targetTexture = null;
        rt.Release();
        Object.DestroyImmediate(rt);
        Debug.Log($"[port] first-person frame written to {outDir}\\scene_firstperson.png");
    }

    /// <summary>Read a render target back to a PNG without disturbing the read target.</summary>
    static void Write(RenderTexture rt, string path)
    {
        var prev = RenderTexture.active;
        RenderTexture.active = rt;
        var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
        tex.Apply();
        RenderTexture.active = prev;
        File.WriteAllBytes(path, tex.EncodeToPNG());
        Object.DestroyImmediate(tex);
    }
}
