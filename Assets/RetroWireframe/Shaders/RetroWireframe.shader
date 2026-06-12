Shader "Custom/Retro Wireframe"
{
    Properties
    {
        _corPrincipal  ("Main Color",  Color)        = (1, 1, 1, 1)
        _corLinha      ("Line Color",  Color)        = (1, 1, 1, 1)
        _larguraLinha  ("Line Width",  Range(0, 1))  = 0.1
        _tParcela      ("Cell Size",   Range(0, 100)) = 1
    }

    SubShader
    {
        // ── Render-state tags ────────────────────────────────────────────────
        Tags
        {
            "Queue"          = "Transparent"
            "RenderType"     = "Transparent"
            "IgnoreProjector"= "True"
        }

        // ── Surface shader program ───────────────────────────────────────────
        CGPROGRAM

        // FIX 1: Include UnityCG.cginc — this is what defines SurfaceOutput,
        //         half, fixed, frac(), step(), lerp() and all CG built-ins.
        //         Without it the compiler cannot resolve any of those symbols,
        //         which silently breaks the shader and Unity shows purple.
        #include "UnityCG.cginc"

        // FIX 2: 'alpha' alone only enables alpha blending.
        //         Use 'alpha:fade' to get proper transparent fade on a Lambert
        //         surface shader; otherwise alpha < 1 has no visible effect.
        #pragma surface surf Lambert alpha:fade

        // ── Uniform declarations (must match Properties block names exactly) ──
        float4 _corLinha;
        float4 _corPrincipal;
        float  _tParcela;
        fixed  _larguraLinha;

        // ── Input struct ──────────────────────────────────────────────────────
        // FIX 3: uv_MainTex is declared but the shader never samples a texture.
        //         Declaring an unused UV set is harmless, but worldPos is what
        //         actually drives the grid — so this is fine as-is.
        struct Input
        {
            float2 uv_MainTex;  // kept; harmless even if unused
            float3 worldPos;    // world-space XZ position for the grid
        };

        // ── Surface function ──────────────────────────────────────────────────
        void surf(Input IN, inout SurfaceOutput o)
        {
            // Compute grid lines along X and Z axes in world space.
            // frac(pos / cellSize) remaps each cell to [0,1].
            // step(lineWidth*2, frac+lineWidth) returns 0 inside the line band,
            // 1 outside — so (1 - val1*val2) isolates the grid lines.
            half val1 = step(_larguraLinha * 2, frac(IN.worldPos.x / _tParcela) + _larguraLinha);
            half val2 = step(_larguraLinha * 2, frac(IN.worldPos.z / _tParcela) + _larguraLinha);
            fixed val = 1 - (val1 * val2);

            o.Albedo = lerp(_corPrincipal.rgb, _corLinha.rgb, val);

            // FIX 4: Blend alpha from whichever colour is active so the
            //         main-colour region can be made transparent independently
            //         of the line colour — previously both were locked to 1.
            o.Alpha  = lerp(_corPrincipal.a,   _corLinha.a,   val);
        }

        ENDCG
    }

    // Fallback only triggers if ALL SubShaders fail; with the fix above it
    // should never be needed, but it is good practice to keep it.
    FallBack "Diffuse"
}
