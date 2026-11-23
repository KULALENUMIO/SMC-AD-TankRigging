// The tank seen straight from above (orthographic): each texel keeps the world height of the topmost surface and how
// much that surface faces up (normal y). MainGunFX draws the tank's meshes with it at a shot (GPU: works for meshes
// without Read/Write) and reads it back to know where dust lies on the hull and the turret.
Shader "Hidden/TankRig/HullHeight"
{
    SubShader
    {
        Pass
        {
            ZWrite On ZTest LEqual Cull Off
            HLSLPROGRAM
            #pragma vertex V
            #pragma fragment F
            #pragma target 4.5
            float4x4 _HH_M;    // object -> world
            float4x4 _HH_VP;   // world -> clip (top-down orthographic, GPU projection)
            struct A { float4 pos : POSITION; float3 n : NORMAL; };
            struct O { float4 cs : SV_POSITION; float y : TEXCOORD0; float3 n : TEXCOORD1; };
            O V(A a)
            {
                O o;
                float4 w = mul(_HH_M, float4(a.pos.xyz, 1.0));
                o.cs = mul(_HH_VP, w);
                o.y = w.y;
                o.n = mul((float3x3)_HH_M, a.n);
                return o;
            }
            float2 F(O i, bool front : SV_IsFrontFace) : SV_Target
            {
                float3 n = normalize(i.n);
                return float2(i.y, abs(n.y));   // either side up (the faces are drawn both ways)
            }
            ENDHLSL
        }
    }
}
