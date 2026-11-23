// Lays the sight overlay (premultiplied) over the camera colour. Runs in an HDRP custom pass after post-processing.
// _SightTan: xy = view-space tangents the overlay covers (half extents), z = 1 to map by view direction
// (XR: every eye sees the symbology at infinity), else the overlay covers the screen 1:1.
Shader "Hidden/TankRig/SightComposite"
{
    HLSLINCLUDE
    #pragma vertex Vert
    #pragma target 4.5
    #pragma only_renderers d3d11 playstation xboxone xboxseries vulkan metal switch
    #include "Packages/com.unity.render-pipelines.high-definition/Runtime/RenderPipeline/RenderPass/CustomPass/CustomPassCommon.hlsl"

    TEXTURE2D(_SightOverlay);
    float4 _SightTan;

    float4 FullScreenPass(Varyings varyings) : SV_Target
    {
        UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(varyings);
        float2 uv = varyings.positionCS.xy * _ScreenSize.zw;     // 0..1 across the screen
        float2 ouv = uv;                                         // the overlay was painted with the same flip
        if (_SightTan.z > 0.5)
        {
            float4 v = mul(UNITY_MATRIX_I_P, float4(uv * 2.0 - 1.0, 0.5, 1.0));
            float3 d = v.xyz / v.w;
            float2 t = d.xy / max(1e-5, -d.z);
            ouv = t / _SightTan.xy * 0.5 + 0.5;
            ouv.y = 1.0 - ouv.y;                                 // screen y runs down here, view-space y up
            if (any(ouv < 0.0) || any(ouv > 1.0)) return 0;
        }
        return SAMPLE_TEXTURE2D_LOD(_SightOverlay, s_linear_clamp_sampler, ouv, 0);
    }
    ENDHLSL

    SubShader
    {
        Tags { "RenderPipeline" = "HDRenderPipeline" }
        Pass
        {
            Name "Composite"
            ZWrite Off ZTest Always Cull Off
            Blend One OneMinusSrcAlpha
            HLSLPROGRAM
            #pragma fragment FullScreenPass
            ENDHLSL
        }
    }
    Fallback Off
}
