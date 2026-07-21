// URP unlit procedural grid with distance-based LOD.
//
// The grid is generated mathematically from the plane's UV (meters), so there
// is no line geometry. Line width is derivative-based (fwidth), which keeps
// distant lines crisp instead of shimmering/moireing. As a fragment gets
// farther from the camera the cell size grows (grid gets sparser) and the line
// thins, per the spec: distant buildings sparse, nearby stairs dense.
//
// Transparent: the space between lines is fully transparent so it overlays in
// AR. ZWrite is off and ZTest is LEqual, so anything that has already written
// depth (a wall, an occluder mesh) hides grid behind it.
//
// Per-surface color and cell size come in through a MaterialPropertyBlock
// (_Grid_Color, _Cell_Size) set by SemanticSurface, so one shared material
// drives every category.
Shader "SemanticMesh/SemanticGrid"
{
    Properties
    {
        [HDR] _Grid_Color ("Grid Color", Color) = (0.2, 1.0, 0.4, 1.0)
        _Base_Color ("Between-Lines Color", Color) = (0, 0, 0, 0)
        _Cell_Size ("Cell Size (m, near)", Float) = 0.25
        _Grid_Thickness ("Line Thickness (0-0.5)", Range(0.001, 0.5)) = 0.03
        _Lod_Start ("LOD Start Distance (m)", Float) = 4.0
        _Lod_End ("LOD End Distance (m)", Float) = 25.0
        _Lod_Coarsen ("LOD Coarsen Multiplier", Range(1, 16)) = 6.0
        _Line_Softness ("Line Softness", Range(0.5, 3.0)) = 1.5
        // 0 = square (sharp), 1 = rounded (softer), 2 = criss-cross (diagonal).
        // Set per-category by the palette; see roadmap 9.1.
        _Grid_Style ("Grid Style (0 sq / 1 round / 2 cross)", Float) = 1.0
        // 0 = grid from mesh UV (traced surfaces, the default). 1 = grid projected
        // top-down from world X/Z, so an imported ground mesh with any/no UVs still
        // gets a seamless grid. No triplanar needed: the ground is horizontal.
        _Projection_Mode ("Projection (0 UV / 1 world XZ)", Float) = 0.0
        // 1 = discard near-vertical faces, which drops the shrinkwrap's sky spikes
        // and building walls on an imported ground so only the terrain grid shows.
        // 0 = draw every face (the default, for traced surfaces).
        _Spike_Discard ("Discard Vertical Faces (0/1)", Float) = 0.0
        // Keep a face only if |world normal.y| is at least this (1 = flat up,
        // 0 = vertical). 0.5 keeps within ~60 degrees of horizontal.
        _Up_Threshold ("Vertical Discard Threshold", Range(0.0, 1.0)) = 0.5
        // 1 = distance LOD on (far cells grow sparse, lines thin). 0 = a uniform
        // grid at the near cell size everywhere, handy for an even overview.
        _Lod_Enable ("Distance LOD (0 off / 1 on)", Float) = 1.0
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "SemanticGridUnlit"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                float3 normalWS : TEXCOORD2;
            };

            CBUFFER_START(UnityPerMaterial)
                float4 _Grid_Color;
                float4 _Base_Color;
                float _Cell_Size;
                float _Grid_Thickness;
                float _Lod_Start;
                float _Lod_End;
                float _Lod_Coarsen;
                float _Line_Softness;
                float _Grid_Style;
                float _Projection_Mode;
                float _Spike_Discard;
                float _Up_Threshold;
                float _Lod_Enable;
            CBUFFER_END

            Varyings vert(Attributes input)
            {
                Varyings output;
                VertexPositionInputs positions = GetVertexPositionInputs(input.positionOS.xyz);
                VertexNormalInputs normals = GetVertexNormalInputs(input.normalOS);
                output.positionHCS = positions.positionCS;
                output.positionWS = positions.positionWS;
                output.normalWS = normals.normalWS;
                output.uv = input.uv;
                return output;
            }

            // Anti-aliased line coverage for one cell axis. uv is in cell units.
            // Returns 0..1 coverage. Derivative-based, so it holds up at any range.
            float grid_coverage(float2 uv_cells, float line_width, float softness)
            {
                float2 uv_deriv = fwidth(uv_cells);
                float2 draw_width = clamp(float2(line_width, line_width), uv_deriv, float2(0.5, 0.5));
                float2 line_aa = uv_deriv * softness;
                float2 grid_uv = 1.0 - abs(frac(uv_cells) * 2.0 - 1.0);
                float2 g = smoothstep(draw_width + line_aa, draw_width - line_aa, grid_uv);
                g *= saturate(float2(line_width, line_width) / draw_width);
                // Fade to a flat fill once cells collapse below a pixel, so the
                // surface reads as a faint wash instead of a moire mess.
                g = lerp(g, float2(line_width, line_width), saturate(uv_deriv * 2.0 - 1.0));
                return lerp(g.x, 1.0, g.y);
            }

            half4 frag(Varyings input) : SV_Target
            {
                // Spike/wall cull (imported ground). Drop any face that leans more
                // than the threshold off horizontal, so the shrinkwrap's vertical
                // sky spikes and building walls vanish and only the terrain grid
                // remains. Off by default (traced surfaces draw every face).
                if (_Spike_Discard > 0.5)
                {
                    float up = abs(normalize(input.normalWS).y);
                    if (up < _Up_Threshold)
                    {
                        discard;
                    }
                }

                float dist = distance(input.positionWS, _WorldSpaceCameraPos);
                // LOD off (_Lod_Enable 0) pins lod_t to 0: uniform near-size grid,
                // no coarsening or line thinning with distance.
                float lod_t = _Lod_Enable > 0.5
                    ? saturate((dist - _Lod_Start) / max(0.001, _Lod_End - _Lod_Start))
                    : 0.0;

                // Farther = bigger cells (sparser) and thinner lines.
                float cell = max(0.001, _Cell_Size) * lerp(1.0, _Lod_Coarsen, lod_t);
                float line_width = _Grid_Thickness * lerp(1.0, 0.35, lod_t);

                // World-XZ projection (mode 1) drives the grid from world position,
                // so an imported ground mesh needs no meter-scaled UVs and lines
                // stay seamless across topology. Mode 0 keeps the per-surface UV
                // path used by traced surfaces. World mode assumes the ground sits
                // at real-world scale; under a scaled twin the cells scale with it.
                float2 plane_uv = _Projection_Mode > 0.5 ? input.positionWS.xz : input.uv;
                float2 uv_cells = plane_uv / cell;

                // Per-category style (roadmap 9.1). The style is uniform across a
                // surface (one value via the property block), so this branch is
                // coherent, not per-pixel divergent. Criss-cross rotates the cell
                // coordinates 45 degrees; square sharpens softness and thins the
                // line; rounded keeps the material defaults.
                bool is_cross = _Grid_Style > 1.5;
                bool is_square = _Grid_Style < 0.5;
                float2 style_cells = is_cross
                    ? float2(uv_cells.x + uv_cells.y, uv_cells.x - uv_cells.y)
                    : uv_cells;
                float style_softness = is_square ? max(0.5, _Line_Softness * 0.5) : _Line_Softness;
                float style_width = is_square ? line_width * 0.7 : line_width;
                float coverage = grid_coverage(style_cells, style_width, style_softness);

                half4 col = lerp(_Base_Color, _Grid_Color, coverage);
                if (col.a <= 0.001)
                {
                    discard;
                }
                return col;
            }
            ENDHLSL
        }
    }

    Fallback Off
}
