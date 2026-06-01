///////////////////////////////////////////////////
// Copyright(c) 2025 CodeWee. All right reserved.
///////////////////////////////////////////////////

Shader "CodeWee/cwGrid(URP)"
{
	Properties
	{
		[Header(Background)]
		_BC ("Background Color", COLOR) = (0,0,0,0)
		[Header(Grid Count)]
		_RX("Grid Count X", Range(1,100) ) = 10
		_RY("Grid Count Y", Range(1,100) ) = 10
		[Toggle]_C("Centered", int) = 1
		[Header(Grid)]
		_Color ("Grid Color", COLOR) = (1,1,1,1)
		_lw("Grid Line Width", Range(0,10) ) = 1
		[Header(SubGrid)]
		_sx("Sub Grid Step X", Range(0.1, 1)) = 0.1
		_sy("Sub Grid Step Y", Range(0.1, 1)) = 0.1
		_sc("Sub Grid Color", Color) = (1,1,1,0.5)
		_slw("Sub Grid Line Width", Range(0,10)) = 0.5
	}
	SubShader
	{
		Tags { 
			"Queue" = "Transparent" 
			"RenderPipeline"="UniversalPipeline"
		}
		HLSLINCLUDE
		#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
		CBUFFER_START(UnityPerMaterial)
			uniform float4 _BC; 
			uniform float _RX;
			uniform float _RY;
			int _C;
			uniform float4 _Color;
			uniform float _lw;
			float _sx;
			float _sy;
			float4 _sc;
			float _slw;
		CBUFFER_END
		struct appdata {
			float4 vertex : POSITION;
			float3 normal : NORMAL;
			float2 uv : TEXCOORD0;
		};
		struct v2f
		{
			float2 uv : TEXCOORD0;
			float4 vertex : SV_POSITION;
		};
		v2f vert (appdata v)
		{
			v2f o;
			o.vertex = TransformObjectToHClip(v.vertex.xyz);
			o.uv = v.uv;
			return o;
		}
			float _gl(float c, float r, float t)
			{
				float cc; 
				if(_C)
					cc = (c - 0.5 + 0.5 / r) * r;
				else
					cc = (c - 0.5) * r;
				float lp = frac(cc); 
				float d = abs(lp - 0.5); 
				float af = fwidth(cc) * 0.5; 
				return smoothstep(t * 0.5 + af, t * 0.5 - af, d);
			}
			inline float4 BlendOneMinusDstColor_One(float4 dst, float4 src)
			{
				return float4(dst.rgb*dst.a + src.rgb * (1.0 - dst.rgb*dst.a), dst.a + src.a*(1-dst.a));
			}
			float4 frag(v2f i) : SV_Target
			{
				float4 acc = float4(0,0,0,0);
				acc = _BC;
				float mg = 0; 
				if(_lw > 0 && _Color.a > 0)
				{
					float gt = _RX *0.002*_lw; 
					float gx = _gl(i.uv.x, _RX, gt);
					float gy = _gl(i.uv.y, _RY, gt);
					mg = max(gx, gy);
				}
				float sg = 0; 
				if(_slw > 0 && _sc.a > 0)
				{
					float sgrx = _RX/_sx; 
					float sgry = _RY/_sy; 
					float sgtx = sgrx *0.002*_slw; 
					float sgty = sgry *0.002*_slw; 
					float sgx = _gl(i.uv.x, sgrx, sgtx);
					float sgy = _gl(i.uv.y, sgry, sgty);
					sg = max(sgx, sgy);
				}
				float ma = _Color.a * mg; 
				float sa = _sc.a * sg * (1.0 - ma); 
				float3 rgb = _sc.rgb * sa +	_Color.rgb * ma;
				float a = sa + ma; 
				float4 pass2 = float4(rgb, a);
				acc = BlendOneMinusDstColor_One(acc, pass2);
				return acc;
			}
			ENDHLSL
		Pass
		{
			Tags { 
				"LightMode" = "cwGridFront" 
			}
			LOD 100
			ZWrite Off
			Cull Front
			Blend SrcAlpha OneMinusSrcAlpha
			HLSLPROGRAM
			#pragma vertex vert
			#pragma fragment frag
			#pragma target 3.0	   
			ENDHLSL
		}
		Pass {	
			Tags { 
				"LightMode" = "UniversalForward" 
			}
			LOD 100
			ZWrite Off
			Cull Back
			Blend SrcAlpha OneMinusSrcAlpha
			HLSLPROGRAM
			#pragma vertex vert
			#pragma fragment frag
			#pragma target 3.0	   
			ENDHLSL
		 }
	}
}
