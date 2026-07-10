Shader "Custom/DepthMask" {
    SubShader {
        // Render right before regular geometry
        Tags {"Queue" = "Geometry-1" "RenderType"="Opaque"}
        
        // Don't draw any color to the screen
        ColorMask 0
        
        // DO write to the depth buffer to hide things behind it
        ZWrite On
        
        Pass {}
    }
}
