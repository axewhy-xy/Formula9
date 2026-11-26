// The map tiles laid on the ground: unlit, so the map keeps its colours, but darkened where the sun's
// shadow falls (by _Formula9ShadowDarkness, which WorldLighting sets from world_look.json).
Shader "Formula9/CityGround"
{
    Properties { _MainTex ("Tile", 2D) = "gray" {} }
    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        Pass
        {
            Tags { "LightMode" = "ForwardBase" }
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fwdbase
            #pragma multi_compile_fog
            #include "UnityCG.cginc"
            #include "AutoLight.cginc"
            sampler2D _MainTex;
            float _Formula9ShadowDarkness;
            // DayCycle's night: 0 by day, nearer 1 as the unlit ground darkens
            float4 _Formula9GroundDim;
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; SHADOW_COORDS(1) UNITY_FOG_COORDS(2) };
            v2f vert(appdata_base v) { v2f o; o.pos = UnityObjectToClipPos(v.vertex); o.uv = v.texcoord.xy; TRANSFER_SHADOW(o); UNITY_TRANSFER_FOG(o, o.pos); return o; }
            fixed4 frag(v2f i) : SV_Target
            {
                fixed4 color = tex2D(_MainTex, i.uv);
                color.rgb *= 1 - _Formula9ShadowDarkness * (1 - SHADOW_ATTENUATION(i));
                color.rgb *= 1 - _Formula9GroundDim.rgb;
                UNITY_APPLY_FOG(i.fogCoord, color);
                return color;
            }
            ENDCG
        }
        // So the ground is in the depth the sun's shadows are worked out against
        UsePass "Legacy Shaders/VertexLit/SHADOWCASTER"
    }
}
