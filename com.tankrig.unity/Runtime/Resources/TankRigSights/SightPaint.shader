// Paints the sight symbology mesh (built by SightPainter) into the overlay texture. Premultiplied alpha.
// uv.z = kind: 0 anti-aliased line (uv.y across the line, uv.w = edge), 1 text (font alpha), 2 fill, 3 sensor image.
Shader "Hidden/TankRig/SightPaint"
{
    Properties
    {
        _FontTex ("Font", 2D) = "white" {}
        _SensorTex ("Sensor", 2D) = "black" {}
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Overlay" }
        Pass
        {
            Name "Paint"
            ZWrite Off ZTest Always Cull Off
            Blend One OneMinusSrcAlpha
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5

            float4x4 _SightMatrix;
            Texture2D _FontTex; SamplerState sampler_FontTex;
            Texture2D _SensorTex; SamplerState sampler_SensorTex;
            float4 _SensorParams;   // x mode (0 colour, 1 mono, 2 thermal), y black hot, z gain, w level
            float4 _SensorParams2;  // x noise, y fixed-pattern noise, z scanlines, w time
            float4 _SensorParams3;  // x vignette, y -, z saturation, w brightness
            float4 _SensorSize;     // w, h, 1/w, 1/h
            float4 _SensorTint;     // linear

            struct A { float3 pos : POSITION; float4 col : COLOR; float4 uv : TEXCOORD0; };
            struct V { float4 pos : SV_POSITION; float4 col : COLOR; float4 uv : TEXCOORD0; };

            V vert(A i)
            {
                V o;
                o.pos = mul(_SightMatrix, float4(i.pos.xy, 0.5, 1));
                o.col = i.col;
                o.uv = i.uv;
                return o;
            }

            float Hash(float2 p)
            {
                p = frac(p * float2(443.897, 441.423));
                p += dot(p, p.yx + 19.19);
                return frac((p.x + p.y) * p.x);
            }

            float3 Sensor(float2 uv)
            {
                float3 c = _SensorTex.Sample(sampler_SensorTex, uv).rgb;
                float l = dot(c, float3(0.2126, 0.7152, 0.0722));
                float mode = _SensorParams.x;
                float2 px = floor(uv * _SensorSize.xy);
                if (mode < 0.5)
                {
                    c = lerp(l.xxx, c, _SensorParams3.z);
                }
                else
                {
                    // thermal look from brightness: a perceptual curve, then gain / level and polarity
                    float v = mode > 1.5 ? sqrt(saturate(l)) : l;
                    v = (v - _SensorParams.w) * _SensorParams.z + 0.5;
                    if (_SensorParams.y > 0.5) v = 1.0 - v;
                    c = saturate(v) * _SensorTint.rgb;
                }
                // sensor noise: per frame per pixel, and a fixed column pattern
                float n = (Hash(px + frac(_SensorParams2.w * 0.731) * 977.0) - 0.5) * _SensorParams2.x
                        + (Hash(float2(px.x, 3.7)) - 0.5) * _SensorParams2.y;
                c = saturate(c + n);
                c *= 1.0 - _SensorParams2.z * (0.5 + 0.5 * cos(uv.y * _SensorSize.y * 6.2831853));
                float2 d = uv * 2.0 - 1.0;
                c *= saturate(1.0 - _SensorParams3.x * dot(d, d) * 0.5);
                return c * _SensorParams3.w;
            }

            float4 frag(V i) : SV_Target
            {
                float a = i.col.a;
                float3 rgb = i.col.rgb;
                float kind = i.uv.z;
                if (kind < 0.5)
                {
                    float v = abs(i.uv.y);
                    float fw = max(fwidth(i.uv.y), 1e-5);
                    a *= saturate((i.uv.w - v) / fw + 0.5);
                }
                else if (kind < 1.5)
                {
                    a *= _FontTex.Sample(sampler_FontTex, i.uv.xy).a;
                }
                else if (kind > 2.5)
                {
                    rgb *= Sensor(i.uv.xy);
                    a = 1.0;
                }
                return float4(rgb * a, a);
            }
            ENDHLSL
        }
    }
}
