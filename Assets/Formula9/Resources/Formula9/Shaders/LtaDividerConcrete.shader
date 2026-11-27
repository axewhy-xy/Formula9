Shader "Formula9/LtaDividerConcrete"
{
    Properties
    {
        _Color ("Concrete tint", Color) = (.68,.67,.63,1)
        _BlockLength ("Paint block length in metres", Float) = .75
    }
    SubShader
    {
        Tags { "Queue"="Geometry+3" "RenderType"="Opaque" }
        Cull Off
        ZWrite On
        Pass
        {
            CGPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            fixed4 _Color;
            float4 _Formula9GroundDim;
            float _BlockLength;
            struct Input { float4 vertex:POSITION; float2 paint:TEXCOORD0; };
            struct Output { float4 position:SV_POSITION; float3 metres:TEXCOORD0; float2 paint:TEXCOORD1; };

            Output vert(Input v)
            {
                Output o;
                o.position = UnityObjectToClipPos(v.vertex);
                o.metres = mul(unity_ObjectToWorld,v.vertex).xyz;
                o.paint = v.paint;
                return o;
            }

            float Grain(float3 position, float frequency)
            {
                float3 p = position * frequency;
                float3 cell = floor(p);
                float3 f = frac(p);
                f = f*f*(3.0-2.0*f);
                return sin(dot(cell+f,float3(2.17,3.79,5.31))) *
                    (1.0-smoothstep(.28,1.25,length(fwidth(p))));
            }

            fixed4 frag(Output i):SV_Target
            {
                float grain = .08*Grain(i.metres,37) + .055*Grain(i.metres,9);
                float phase = i.paint.x / max(.05,_BlockLength);
                float wave = sin(UNITY_PI*phase);
                float smoothing = max(.012, fwidth(wave));
                float white = smoothstep(-smoothing,smoothing,wave);
                float3 paint = lerp(float3(.11,.105,.10),float3(.84,.83,.77),white);
                float3 tint = lerp(_Color.rgb,paint,saturate(i.paint.y));
                return fixed4(saturate(tint + grain) * (1.0 - _Formula9GroundDim.rgb), 1);
            }
            ENDCG
        }
    }
    Fallback Off
}
