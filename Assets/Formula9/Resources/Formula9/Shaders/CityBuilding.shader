// The extruded city. Each building style (Resources/Formula9/building_styles.json) has a wall texture for
// the ground floor and a facade texture above it, one slice of each texture array, both tiled by their
// real size in metres and with normal maps so ledges, frames and recessed windows catch the light.
// CityMesh gives each vertex (metres along the wall, metres up, style, 1 on a roof) in its first texture
// coordinate, the building's body colour as its vertex colour and its accent colour in its fourth
// texture coordinate. A texture's alpha says which of those paints it: 1 body, 0.5 accent, 0 neither
// (glass and frames). Roofs are plain body colour. _Plain turns the textures off, for the shuttle, stop
// and depot markers that share this material.
Shader "Formula9/CityBuilding"
{
    Properties
    {
        _Color ("Color", Color) = (1, 1, 1, 1)
        _Walls ("Ground-floor walls, one slice per style", 2DArray) = "" {}
        _WallNormals ("Ground-floor wall normals", 2DArray) = "" {}
        _Facades ("Facades, one slice per style", 2DArray) = "" {}
        _FacadeNormals ("Facade normals", 2DArray) = "" {}
        _Plain ("Plain colour only", Float) = 0
        // A little light of its own, so walls in deep shadow keep some colour
        _SelfLight ("Self light", Range(0, 1)) = 0.06
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        CGPROGRAM
        #pragma surface surf Lambert fullforwardshadows vertex:vert
        #pragma target 3.5
        #pragma require 2darray
        UNITY_DECLARE_TEX2DARRAY(_Walls);
        UNITY_DECLARE_TEX2DARRAY(_WallNormals);
        UNITY_DECLARE_TEX2DARRAY(_Facades);
        UNITY_DECLARE_TEX2DARRAY(_FacadeNormals);
        fixed4 _Color;
        float _Plain, _SelfLight;
        // Per style, set by CityView from the styles file (BuildingStyles.MaxStyles long):
        // what one copy covers in metres, the wall's (width, height) in xy and the facade's in zw
        float4 _Tiles[32];
        // x: the plain ground floor's height in metres; y: how strongly the normal maps bend the light
        float4 _StyleSettings[32];
        struct Input { fixed4 color : COLOR; float4 wall; float3 accent; };

        void vert(inout appdata_full v, out Input o)
        {
            UNITY_INITIALIZE_OUTPUT(Input, o);
            o.wall = v.texcoord;
            o.accent = v.texcoord3.rgb;
        }

        // A little light of its own, so walls in shadow keep their colour instead of going black
        void surf(Input input, inout SurfaceOutput output)
        {
            fixed3 body = _Color.rgb * input.color.rgb;
            float flat = saturate(max(input.wall.w, _Plain));
            int style = (int)round(input.wall.z);
            float4 tile = _Tiles[style];
            float4 settings = _StyleSettings[style];
            float2 metres = input.wall.xy;
            // The facade's floors start where the ground floor ends
            float3 wallAt = float3(metres / tile.xy, style);
            float3 facadeAt = float3(float2(metres.x, metres.y - settings.x) / tile.zw, style);
            // Both are sampled and then chosen between, which keeps mipmapping right at the edge
            float upper = step(settings.x, metres.y);
            float4 color = lerp(UNITY_SAMPLE_TEX2DARRAY(_Walls, wallAt), UNITY_SAMPLE_TEX2DARRAY(_Facades, facadeAt), upper);
            float3 normal = lerp(UNITY_SAMPLE_TEX2DARRAY(_WallNormals, wallAt).xyz, UNITY_SAMPLE_TEX2DARRAY(_FacadeNormals, facadeAt).xyz, upper) * 2 - 1;
            normal.xy *= settings.y;
            float bodyShare = saturate(color.a * 2 - 1);
            float accentShare = saturate(1 - abs(color.a * 2 - 1));
            fixed3 painted = color.rgb * lerp(fixed3(1, 1, 1), body, bodyShare) * lerp(fixed3(1, 1, 1), input.accent, accentShare);
            output.Albedo = lerp(painted, body, flat);
            output.Normal = normalize(lerp(normal, float3(0, 0, 1), flat));
            output.Emission = output.Albedo * _SelfLight;
        }
        ENDCG
    }
    FallBack "Diffuse"
}
