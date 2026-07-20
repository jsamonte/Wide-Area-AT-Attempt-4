// Invisible depth writer. Put this material on a solid mesh (a wall, the
// wireframe environment, anything that should hide things behind it) as an
// extra material slot, and it writes depth without drawing any color. Because
// it renders in the opaque queue (before transparent grids and gems), the
// depth it lays down makes any transparent surface behind it fail ZTest and
// disappear. This is what stops grids and gems from showing through walls.
Shader "SemanticMesh/DepthOccluder"
{
    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "Queue" = "Geometry-1"
            "RenderPipeline" = "UniversalPipeline"
        }

        Pass
        {
            Name "DepthOcclude"
            Tags { "LightMode" = "UniversalForward" }

            ColorMask 0
            ZWrite On
            ZTest LEqual
            Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                output.positionHCS = GetVertexPositionInputs(input.positionOS.xyz).positionCS;
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                return half4(0, 0, 0, 0);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
