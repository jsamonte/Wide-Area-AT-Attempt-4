// TEMPORARY DIAGNOSTIC / FIX HELPER - safe to delete once the graphics API change has stuck.
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Gsplat;

public static class GsplatDiagnostic
{
    [MenuItem("Tools/Gsplat Diagnostic")]
    public static void Run()
    {
        var s = GsplatSettings.Instance;
        Debug.Log($"[DIAG] gfxDevice={SystemInfo.graphicsDeviceType} supportsCompute={SystemInfo.supportsComputeShaders}");
        Debug.Log($"[DIAG] settings.Valid={(s != null && s.Valid)} sorter.Valid={GsplatSorter.Instance.Valid}");

        var cs = s != null ? s.ComputeShader : null;
        if (cs)
        {
            foreach (var k in new[] { "InitPayload", "InitDeviceRadixSort", "Upsweep", "Scan", "Downsweep" })
            {
                int idx = cs.FindKernel(k);
                Debug.Log($"[DIAG]   kernel {k}: index={idx} IsSupported={(idx >= 0 ? cs.IsSupported(idx).ToString() : "n/a")}");
            }
        }

        foreach (var r in Object.FindObjectsByType<GsplatRenderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            Debug.Log($"[DIAG] renderer '{r.name}' Valid={r.Valid} SplatCount={r.SplatCount} " +
                      $"assetBounds={(r.GsplatAsset ? r.GsplatAsset.Bounds.ToString() : "-")} rendererBounds={r.Bounds}");
    }

    [MenuItem("Tools/Gsplat Set Windows Graphics API To Vulkan")]
    public static void SetVulkan()
    {
        foreach (var target in new[] { BuildTarget.StandaloneWindows64, BuildTarget.StandaloneWindows })
        {
            PlayerSettings.SetUseDefaultGraphicsAPIs(target, false);
            PlayerSettings.SetGraphicsAPIs(target, new[] { GraphicsDeviceType.Vulkan });
            Debug.Log($"[FIX] {target} graphics APIs -> Vulkan");
        }

        AssetDatabase.SaveAssets();
        Debug.Log("[FIX] Done. RESTART the Unity Editor for this to take effect.");
    }
}
