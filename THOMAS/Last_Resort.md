# Fallback Plan: URP Screen-Space Edge Detection (No Mesh Editing)

## Objective
Implement a global screen-space wireframe/outline effect using a URP `ScriptableRendererFeature`. This approach ignores the messy geometry of the raw XGRIDS scan and instead uses screen depth and normal data to mathematically draw edges over buildings and ground.

## Architecture & Workflow
1.  **Depth/Normal Pass:** The raw scan mesh will be rendered using a base material that is visually transparent but still writes to the Depth and Normal buffers (Z-write enabled, Color mask 0).
2.  **Render Feature:** A custom C# `ScriptableRendererFeature` and `ScriptableRenderPass` will inject a fullscreen post-processing pass just before the transparent rendering queue.
3.  **Edge Detection Shader:** A custom HLSL fullscreen shader will sample `_CameraDepthTexture` and `_CameraNormalsTexture`. It will apply a Sobel filter to detect sharp changes in depth (object edges) or normals (corners/slopes), rendering those detected pixels as solid lines.

## Technical Requirements for Magic Leap 2
*   **Distance Fading:** The fragment shader must calculate the linear depth or distance from the camera. The `edge_alpha` must fade to 0 at a configurable distance threshold. Without this, distant buildings will render as a noisy, solid block of lines and cause visual fatigue in AR.
*   **XR Optimization:** The Render Feature must explicitly support Single-Pass Instanced rendering (SPI) to ensure the effect renders correctly in both eyes without halving the framerate.
*   **Property Exposure:** Expose `edge_color`, `depth_threshold`, `normal_threshold`, and `fade_distance` to the material inspector for live tuning in the editor.

## Coding Standards
*   Strictly use `snake_case` for all variables, fields, and properties across both the C# scripts and the HLSL shader code.
*   Class names and structs should remain PascalCase per standard C# conventions.
*   Ensure zero runtime allocations in the render loop; do not use the `new` keyword or generate garbage within the `Execute` method of the render pass.