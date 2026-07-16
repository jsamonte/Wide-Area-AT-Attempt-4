using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using Gsplat;

namespace PLUME.Tools
{
    public class GsplatToMeshConverter : EditorWindow
    {
        [MenuItem("Tools/Convert Gsplat to Point Cloud Mesh")]
        public static void ConvertGsplatToMesh()
        {
            // Find selected GsplatRenderer or any in scene
            GsplatRenderer renderer = Selection.activeGameObject?.GetComponent<GsplatRenderer>();
            if (renderer == null)
            {
                renderer = FindObjectOfType<GsplatRenderer>();
                if (renderer == null)
                {
                    EditorUtility.DisplayDialog("Error", "No GsplatRenderer found in the scene.", "OK");
                    return;
                }
            }

            if (renderer.m_Asset == null)
            {
                EditorUtility.DisplayDialog("Error", "GsplatRenderer has no asset assigned.", "OK");
                return;
            }

            GsplatAssetSpark sparkAsset = renderer.m_Asset as GsplatAssetSpark;
            GsplatAssetUncompressed uncompressedAsset = renderer.m_Asset as GsplatAssetUncompressed;

            if (sparkAsset == null && uncompressedAsset == null)
            {
                EditorUtility.DisplayDialog("Error", "Only Spark and Uncompressed formats are supported currently.", "OK");
                return;
            }

            uint splatCount = renderer.m_Asset.SplatCount;
            
            // Unity 32-bit index buffers support up to 4 billion vertices, but let's downsample if > 16 million to keep file size reasonable
            int maxVertices = 16000000;
            int step = 1;
            if (splatCount > maxVertices)
            {
                step = Mathf.CeilToInt((float)splatCount / maxVertices);
            }

            int finalCount = (int)(splatCount / step);

            Vector3[] vertices = new Vector3[finalCount];
            Color32[] colors = new Color32[finalCount];
            int[] indices = new int[finalCount];

            EditorUtility.DisplayProgressBar("Converting Gsplat", "Extracting data...", 0.0f);

            try
            {
                int vertexIndex = 0;
                for (int i = 0; i < splatCount; i += step)
                {
                    if (vertexIndex >= finalCount) break;

                    if (sparkAsset != null)
                    {
                        var packed = sparkAsset.PackedSplats[i];
                        
                        // Extract color
                        byte r = (byte)(packed.x & 0xFF);
                        byte g = (byte)((packed.x >> 8) & 0xFF);
                        byte b = (byte)((packed.x >> 16) & 0xFF);
                        byte a = (byte)((packed.x >> 24) & 0xFF);
                        
                        // Extract position
                        ushort hx = (ushort)(packed.y & 0xFFFF);
                        ushort hy = (ushort)(packed.y >> 16);
                        ushort hz = (ushort)(packed.z & 0xFFFF);
                        
                        vertices[vertexIndex] = new Vector3(
                            Mathf.HalfToFloat(hx),
                            Mathf.HalfToFloat(hy),
                            Mathf.HalfToFloat(hz)
                        );
                        colors[vertexIndex] = new Color32(r, g, b, 255); // Force opaque for point cloud
                    }
                    else if (uncompressedAsset != null)
                    {
                        vertices[vertexIndex] = uncompressedAsset.Positions[i];
                        Vector4 c = uncompressedAsset.Colors[i];
                        
                        // Uncompressed color is already normalized RGB? Actually let's just use it
                        // According to gsplat standard, they might be SH0 or Sigmoid. Let's assume linear 0-1 for simplicity or raw
                        // For uncompressed, the code doesn't show how they are encoded. Spark uses sigmoid.
                        // We will try standard Color conversion.
                        colors[vertexIndex] = new Color32(
                            (byte)Mathf.Clamp(c.x * 255f, 0, 255),
                            (byte)Mathf.Clamp(c.y * 255f, 0, 255),
                            (byte)Mathf.Clamp(c.z * 255f, 0, 255),
                            255
                        );
                    }

                    indices[vertexIndex] = vertexIndex;
                    vertexIndex++;

                    if (i % 100000 == 0)
                    {
                        EditorUtility.DisplayProgressBar("Converting Gsplat", $"Extracting data {i}/{splatCount}", (float)i / splatCount);
                    }
                }

                EditorUtility.DisplayProgressBar("Converting Gsplat", "Building Mesh...", 0.9f);

                Mesh mesh = new Mesh();
                mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                mesh.vertices = vertices;
                mesh.colors32 = colors;
                mesh.SetIndices(indices, MeshTopology.Points, 0);
                mesh.RecalculateBounds();

                string dir = "Assets/Prefab";
                if (!AssetDatabase.IsValidFolder("Assets/Prefab"))
                {
                    AssetDatabase.CreateFolder("Assets", "Prefab");
                }

                string meshPath = dir + "/DigitalTwinPointCloud.asset";
                AssetDatabase.CreateAsset(mesh, meshPath);
                AssetDatabase.SaveAssets();

                // Create the child GameObject
                GameObject pointCloudObj = new GameObject("PLUME_PointCloud_Mesh");
                pointCloudObj.transform.SetParent(renderer.transform, false);

                MeshFilter mf = pointCloudObj.AddComponent<MeshFilter>();
                mf.sharedMesh = mesh;

                MeshRenderer mr = pointCloudObj.AddComponent<MeshRenderer>();
                
                // Assign shader
                Shader pointShader = Shader.Find("Custom/PointCloudVertexColor");
                if (pointShader == null) pointShader = Shader.Find("Unlit/Color"); // Fallback
                
                Material pointMat = new Material(pointShader);
                AssetDatabase.CreateAsset(pointMat, dir + "/DigitalTwinPointCloudMat.mat");
                mr.sharedMaterial = pointMat;

                Selection.activeGameObject = pointCloudObj;

                EditorUtility.DisplayDialog("Success", $"Created point cloud mesh with {finalCount} vertices.", "OK");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }
    }
}
