# Project Specification: Semantic Mesh Retopology and Grid Rendering Tool

## Objective
Create a custom Unity Editor tool and an accompanying URP shader to optimize a high-density environmental mesh (~125,220 ft²) for the Magic Leap 2. The tool must allow a user to manually trace low-poly planes over the raw mesh. These planes will use a custom shader to project a dynamic, distance-based LOD grid.

## Coding Standards
* Use `snake_case` for all variables, fields, and properties. 
* Classes and structs should remain PascalCase per standard C# conventions.
* Ensure all scripts are optimized for mobile XR performance (avoiding heavy per-frame garbage collection).

## Phase 1: The Editor Tool (Plane Drawing)
Create a custom Unity Editor Window and Scene View tool with the following workflow:
1. **Raycast Placement:** The user clicks in the Scene View to raycast against the high-res target mesh.
2. **Point Placement:** Each click drops a vertex point.
3. **Polygon Generation:** Once a shape is closed, the tool uses a triangulation algorithm (e.g., Ear Clipping) to generate a flat, low-poly `Mesh` bridging those points.
4. **Data Assignment:** Attach a custom `MonoBehaviour` (e.g., `semantic_surface`) to the generated plane.
5. **Categorization:** The `semantic_surface` script must contain an `enum` for `surface_type` (Walkable, Stairs, Grass, Building, etc.).

## Phase 2: The URP Grid Shader
Do not generate the grid using geometry, meshes, or LineRenderers. Create a custom URP Shader (Shader Graph or HLSL) to be applied to the generated planes.
1. **Procedural Grid:** The shader mathematically generates a grid pattern based on world-space or object-space coordinates.
2. **Color Coding:** Expose a color property so different `surface_type` categories can have different colored grids.
3. **Distance-Based LOD:** 
   * Calculate the distance between the fragment and the `_WorldSpaceCameraPos`.
   * Expose properties to control `grid_density` and line thickness based on this distance. 
   * Distant buildings should show a very sparse grid; nearby stairs should show a dense grid.
4. **Transparency/Blend:** The empty space between the grid lines must be transparent to overlay naturally in AR.

## Execution Steps for Claude
1. Please generate the `SemanticSurface` MonoBehaviour and the related enum.
2. Write the Unity Editor script that handles the Scene View raycasting, point dropping, and mesh generation.
3. Provide the HLSL code or Shader Graph node structure for the LOD Grid Shader.
4. Do not overcomplicate the triangulation; assume mostly convex or simple concave 2D shapes projected on a 3D plane.