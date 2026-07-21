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
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
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
            CBUFFER_END

            Varyings vert(Attributes input)
            {
                Varyings output;
                VertexPositionInputs positions = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionHCS = positions.positionCS;
                output.positionWS = positions.positionWS;
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
                float dist = distance(input.positionWS, _WorldSpaceCameraPos);
                float lod_t = saturate((dist - _Lod_Start) / max(0.001, _Lod_End - _Lod_Start));

                // Farther = bigger cells (sparser) and thinner lines.
                float cell = max(0.001, _Cell_Size) * lerp(1.0, _Lod_Coarsen, lod_t);
                float line_width = _Grid_Thickness * lerp(1.0, 0.35, lod_t);

                float2 uv_cells = input.uv / cell;

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
