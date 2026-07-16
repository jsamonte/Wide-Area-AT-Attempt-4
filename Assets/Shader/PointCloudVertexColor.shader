Shader "Custom/PointCloudVertexColor"
{
    Properties
    {
        _PointSize ("Point Size", Float) = 0.05
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        LOD 100

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma geometry geom
            
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float4 color : COLOR;
            };

            struct v2g
            {
                float4 vertex : SV_POSITION;
                float4 color : COLOR;
            };

            struct g2f
            {
                float4 pos : SV_POSITION;
                float4 color : COLOR;
            };

            float _PointSize;

            v2g vert (appdata v)
            {
                v2g o;
                o.vertex = v.vertex;
                o.color = v.color;
                return o;
            }
            
            [maxvertexcount(4)]
            void geom(point v2g input[1], inout TriangleStream<g2f> triStream)
            {
                float3 up = float3(0, 1, 0);
                float3 right = float3(1, 0, 0);
                
                // Keep points billboarded to camera
                float3 look = _WorldSpaceCameraPos - mul(unity_ObjectToWorld, input[0].vertex).xyz;
                look = normalize(look);
                right = normalize(cross(up, look));
                up = cross(look, right);
                
                float halfS = _PointSize * 0.5;
                
                float4 v[4];
                v[0] = float4(input[0].vertex.xyz + right * halfS - up * halfS, 1.0);
                v[1] = float4(input[0].vertex.xyz + right * halfS + up * halfS, 1.0);
                v[2] = float4(input[0].vertex.xyz - right * halfS - up * halfS, 1.0);
                v[3] = float4(input[0].vertex.xyz - right * halfS + up * halfS, 1.0);
                
                g2f pIn;
                pIn.color = input[0].color;
                
                pIn.pos = UnityObjectToClipPos(v[0]);
                triStream.Append(pIn);
                
                pIn.pos = UnityObjectToClipPos(v[1]);
                triStream.Append(pIn);
                
                pIn.pos = UnityObjectToClipPos(v[2]);
                triStream.Append(pIn);
                
                pIn.pos = UnityObjectToClipPos(v[3]);
                triStream.Append(pIn);
            }

            fixed4 frag (g2f i) : SV_Target
            {
                // Linear to Gamma conversion might be needed depending on project color space, 
                // but let's just output raw color for now.
                return i.color;
            }
            ENDCG
        }
    }
    FallBack "Unlit/Color"
}
