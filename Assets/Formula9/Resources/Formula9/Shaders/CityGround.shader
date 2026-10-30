// The map tiles laid on the ground: unlit, so roads keep their map colours.
Shader "Formula9/CityGround"
{
    Properties { _MainTex ("Tile", 2D) = "gray" {} }
    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            struct v2f { float4 position : SV_POSITION; float2 uv : TEXCOORD0; };
            v2f vert(appdata_base input) { v2f output; output.position = UnityObjectToClipPos(input.vertex); output.uv = input.texcoord.xy; return output; }
            fixed4 frag(v2f input) : SV_Target { return tex2D(_MainTex, input.uv); }
            ENDCG
        }
    }
}
