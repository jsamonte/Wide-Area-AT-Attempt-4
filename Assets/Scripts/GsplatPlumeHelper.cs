using UnityEngine;
using Gsplat;

[RequireComponent(typeof(GsplatRenderer))]
[ExecuteAlways]
public class GsplatPlumeHelper : MonoBehaviour
{
    private GsplatRenderer m_Renderer;

    void Awake()
    {
        m_Renderer = GetComponent<GsplatRenderer>();

#if !UNITY_EDITOR
        // Destroy the Gaussian splat renderer on actual device builds (ML2) so it doesn't cause lag
        if (m_Renderer != null) Destroy(m_Renderer);

        // Also find the generated point cloud mesh (if it exists) and disable it on device
        Transform pointCloudObj = transform.Find("PLUME_PointCloud_Mesh");
        if (pointCloudObj != null)
        {
            MeshRenderer mr = pointCloudObj.GetComponent<MeshRenderer>();
            if (mr != null) mr.enabled = false;
        }
#endif
    }
}
