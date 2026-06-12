using UnityEngine;

/// <summary>
/// Draws a dynamic blue wireframe over the attached MeshFilter's mesh
/// using Unity's low-level GL (Graphics Library) API.
///
/// HOW TO USE:
///   1. Attach this script to any GameObject that has a MeshFilter component.
///   2. Optionally tweak wireframeColor and lineWidth in the Inspector.
///   3. Press Play — the wireframe is drawn every frame in OnRenderObject().
///
/// REQUIREMENTS:
///   - The GameObject must have a MeshFilter (with a valid mesh).
///   - A material with a vertex-lit or unlit shader is created automatically.
/// </summary>
[RequireComponent(typeof(MeshFilter))]
public class WireframeRenderer : MonoBehaviour
{
    [Header("Wireframe Settings")]
    [Tooltip("Color of the wireframe lines. Defaults to blue.")]
    public Color wireframeColor = Color.blue;

    [Tooltip("If true, the original mesh renderer is hidden so only the wireframe shows.")]
    public bool hideOriginalMesh = false;

    // ── Private state ────────────────────────────────────────────────────────

    private Material _wireMaterial;   // GL requires a material to set the pass
    private Mesh     _mesh;           // Cached reference to the mesh
    private int[]    _triangles;      // Triangle index buffer
    private Vector3[] _vertices;      // Vertex position buffer

    // ─────────────────────────────────────────────────────────────────────────
    // Unity lifecycle
    // ─────────────────────────────────────────────────────────────────────────

    private void Awake()
    {
        // Cache mesh data once — avoids per-frame managed allocations
        MeshFilter mf = GetComponent<MeshFilter>();
        if (mf == null || mf.sharedMesh == null)
        {
            Debug.LogError("[WireframeRenderer] No MeshFilter / mesh found on " + gameObject.name);
            enabled = false;
            return;
        }

        _mesh      = mf.sharedMesh;
        _triangles = _mesh.triangles;   // int[]  — 3 indices per triangle
        _vertices  = _mesh.vertices;    // Vector3[] in local space

        // Build a minimal unlit material for GL rendering
        _wireMaterial = CreateWireMaterial();

        // Optionally suppress the default MeshRenderer so only the wireframe is visible
        if (hideOriginalMesh)
        {
            MeshRenderer mr = GetComponent<MeshRenderer>();
            if (mr != null) mr.enabled = false;
        }
    }

    /// <summary>
    /// Called by Unity after all regular rendering is complete for this camera.
    /// Drawing here ensures the wireframe renders on top of (or blended with)
    /// the normal geometry without interfering with the render pipeline.
    /// </summary>
    private void OnRenderObject()
    {
        if (_wireMaterial == null || _triangles == null) return;

        // Apply the first (and only) pass of our unlit material.
        // This sets the GPU state (blend mode, depth test, etc.).
        _wireMaterial.SetPass(0);

        // Push the object-to-world matrix so every GL vertex we supply
        // is treated as local-space and automatically transformed correctly.
        GL.PushMatrix();
        GL.MultMatrix(transform.localToWorldMatrix);

        // Tell GL we are about to submit line-segment pairs.
        // Each Begin/End block is one draw call.
        GL.Begin(GL.LINES);
        GL.Color(wireframeColor);   // Single colour for all lines in this block

        // Iterate over every triangle (3 indices at a time) and emit its 3 edges.
        // Each edge = 2 GL vertices.
        for (int i = 0; i < _triangles.Length; i += 3)
        {
            Vector3 v0 = _vertices[_triangles[i]];
            Vector3 v1 = _vertices[_triangles[i + 1]];
            Vector3 v2 = _vertices[_triangles[i + 2]];

            // Edge 0 → 1
            GL.Vertex(v0);
            GL.Vertex(v1);

            // Edge 1 → 2
            GL.Vertex(v1);
            GL.Vertex(v2);

            // Edge 2 → 0
            GL.Vertex(v2);
            GL.Vertex(v0);
        }

        GL.End();
        GL.PopMatrix();
    }

    private void OnDestroy()
    {
        // Clean up the dynamically created material to avoid memory leaks
        if (_wireMaterial != null)
            Destroy(_wireMaterial);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates an unlit, vertex-colour material suitable for GL drawing.
    /// Uses a hidden built-in shader that Unity always ships with.
    /// </summary>
    private static Material CreateWireMaterial()
    {
        // "Hidden/Internal-Colored" is a Unity built-in shader that respects
        // GL.Color() and does no lighting calculations — perfect for debug lines.
        Shader shader = Shader.Find("Hidden/Internal-Colored");
        if (shader == null)
        {
            // Fallback: any unlit shader works, but colour will be ignored
            shader = Shader.Find("Unlit/Color");
            Debug.LogWarning("[WireframeRenderer] 'Hidden/Internal-Colored' not found; " +
                             "falling back to 'Unlit/Color'. Wireframe colour may not apply.");
        }

        Material mat = new Material(shader)
        {
            hideFlags = HideFlags.HideAndDontSave   // Don't pollute the project
        };

        // Render over existing geometry without writing to the depth buffer,
        // so the wireframe is always visible regardless of draw order.
        mat.SetInt("_SrcBlend",  (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        mat.SetInt("_DstBlend",  (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        mat.SetInt("_Cull",      (int)UnityEngine.Rendering.CullMode.Off);   // Show back faces
        mat.SetInt("_ZWrite",    0);   // Don't occlude other geometry

        return mat;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Live mesh updates (optional)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Call this at runtime if the mesh is deformed (e.g. skinned mesh baking,
    /// procedural generation) and you need the wireframe to reflect the changes.
    /// </summary>
    public void RefreshMeshData()
    {
        if (_mesh == null) return;
        _triangles = _mesh.triangles;
        _vertices  = _mesh.vertices;
    }
}
