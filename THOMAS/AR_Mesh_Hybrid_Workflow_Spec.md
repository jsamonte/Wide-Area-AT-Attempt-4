# Hybrid Mesh Retopology & Grid Integration Workflow

## Overview
This document outlines the finalized hybrid strategy for managing the high-density XGRIDS scan for the Magic Leap 2. To maintain framerate and visual clarity in the AR study, the environment will be split into two distinct rendering approaches. 

## Component 1: The Ground (Draped Proxy Mesh)
The uneven ground terrain (streets, sidewalks, dirt) has been processed externally using a high-density shrinkwrap projection. 
*   **Action Required:** Import the provided draped ground FBX. The user will manually delete any stretched vertical vertices from this mesh in Blender before import.
*   Apply the custom URP World-Space LOD Grid Shader to this imported ground mesh.
*   *Note:* Do not attempt to dynamically retopologize the ground using the custom Editor tool, as the organic slopes are best handled by this pre-calculated draped mesh.

## Component 2: The Buildings (Semantic Plane Tool)
Because the vertical walls in the draped mesh are infinitely stretched and unusable, all vertical surfaces (buildings, walls, hard structures) will be handled manually in-editor.
*   **Action Required:** Finalize the custom Unity Editor Scene View tool. The user will use this tool to manually snap flat, low-poly planes against the vertical faces of the raw scan.
*   The generated planes must automatically receive the `semantic_surface` script and be categorized via the `surface_type` enum (e.g., `building_wall`, `stairs`).
*   Apply the exact same URP World-Space LOD Grid Shader to these generated planes.

## Unified Shader Requirements
The URP LOD Grid Shader must be universally applicable to both the imported ground mesh and the generated building planes.
*   It must rely entirely on world-space coordinates `(world_pos.x, world_pos.y, world_pos.z)` to project the grid, ensuring seamless lines regardless of the underlying mesh topology.
*   *Crucial for Buildings:* The shader must account for vertical projections (e.g., implementing triplanar mapping logic). A simple X/Z modulo will smear on vertical walls; the shader must calculate the grid across X/Y or Z/Y axes based on the surface normal.

## Coding Standards & Performance
*   Maintain strict `snake_case` for all variables, fields, and properties across all newly generated C# and HLSL scripts. 
*   Ensure memory allocation is minimized during runtime to prevent garbage collection frame-drops on the untethered headset.
