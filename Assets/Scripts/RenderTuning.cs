using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Frame-budget settings that have to be right for the street to run at all.
///
/// Applied at load rather than saved into the pipeline asset deliberately: a
/// static-function hook takes effect on the next Play without anyone rebuilding
/// the scene, which matters when the person who found the problem is the one
/// holding the editor open.
///
/// The big one is shadow distance. The level is 1.83M triangles in 45 merged
/// meshes, and URP's default 150 m shadow range renders essentially all of it a
/// second time every frame — for a street whose usable sightlines are about 60 m.
/// </summary>
public static class RenderTuning
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Apply()
    {
        var urp = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
        if (urp != null)
        {
            if (urp.shadowDistance > 45f) urp.shadowDistance = 45f;
            // Cascades are only worth splitting over a range this short if the
            // near one is tight; two is the point where the cost stops paying.
            if (urp.shadowCascadeCount > 2) urp.shadowCascadeCount = 2;
        }

        // Uncapped, so the measured frame time is the machine's and not a vsync
        // artefact — the same reason upstream profiles at real device DPR.
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = -1;
    }
}
