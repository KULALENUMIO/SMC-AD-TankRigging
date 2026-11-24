// The blast wave: a thin spherical shell of compressed air (higher refractive index) expanding from the muzzle.
// Seen through it the background shifts radially; strongest where the view grazes the shell (the projected rim).
Shader "Hidden/TankRig/BlastShock"
{
    HLSLINCLUDE

    #pragma vertex Vert
    #pragma target 4.5
    #pragma only_renderers d3d11 vulkan metal

    #include "Packages/com.unity.render-pipelines.high-definition/Runtime/RenderPipeline/RenderPass/CustomPass/CustomPassCommon.hlsl"

    TEXTURE2D_X(_MB_Src);
    float4 _MB_Shock;      // xyz centre (absolute world), w radius (m)
    float4 _MB_Shock2;     // x strength (uv), y shell thickness (m), z brightness of the shell

    float4 FullScreenPass(Varyings varyings) : SV_Target
    {
        UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(varyings);
        float depth = LoadCameraDepth(varyings.positionCS.xy);
        PositionInputs posInput = GetPositionInput(varyings.positionCS.xy, _ScreenSize.zw, depth, UNITY_MATRIX_I_VP, UNITY_MATRIX_V);
        float2 uv = posInput.positionNDC;
        float3 src = SAMPLE_TEXTURE2D_X_LOD(_MB_Src, s_linear_clamp_sampler, uv * _RTHandleScale.xy, 0).rgb;

        float3 camAbs = GetAbsolutePositionWS(float3(0, 0, 0));
        float3 pAbs = GetAbsolutePositionWS(posInput.positionWS);
        float3 dir = pAbs - camAbs;
        float sceneDist = length(dir);
        dir /= max(sceneDist, 1e-5);
        if (depth == UNITY_RAW_FAR_CLIP_VALUE) sceneDist = 1e6;

        float R = _MB_Shock.w;
        float3 oc = _MB_Shock.xyz - camAbs;
        float tca = dot(oc, dir);
        float b = length(oc - dir * tca);           // closest approach of the view ray to the centre
        float w = _MB_Shock2.y;
        float x = (b - R) / w;
        if (abs(x) > 3.0 || sceneDist < tca - R) return float4(src, 1);   // off the shell, or the shell is behind geometry

        // path length through the shell grows sharply towards the rim; its derivative bends the ray
        float g = exp(-x * x);
        float bend = -x * g * _MB_Shock2.x;
        float3 cRel = GetCameraRelativePositionWS(_MB_Shock.xyz);
        float2 cUV = ComputeNormalizedDeviceCoordinates(cRel, UNITY_MATRIX_VP);
        float2 radial = uv - cUV;
        radial = radial / max(length(radial), 1e-4);
        float2 uv2 = uv + radial * bend;
        float3 col = SAMPLE_TEXTURE2D_X_LOD(_MB_Src, s_linear_clamp_sampler, clamp(uv2, 0, 1) * _RTHandleScale.xy, 0).rgb;
        col *= 1.0 + _MB_Shock2.z * g;                // the compressed shell catches a little more light
        return float4(col, 1);
    }

    ENDHLSL

    SubShader
    {
        Tags{ "RenderPipeline" = "HDRenderPipeline" }
        Pass
        {
            Name "MuzzleBlastShock"
            ZWrite Off
            ZTest Always
            Blend Off
            Cull Off
            HLSLPROGRAM
                #pragma fragment FullScreenPass
            ENDHLSL
        }
    }
    Fallback Off
}
