// The extruded city: walls carry a wall texture on the ground floor and window texture above it, both
// tiled by their real size in metres and tinted by the building's colour; roofs stay plain colour.
// CityMesh gives each vertex (metres along the wall, metres up, texture pair, 1 on a roof) in its first
// texture coordinate. Pair 0 is homes, pair 1 everything else. _Plain turns the textures off, for the
// shuttle and stop markers that share this material.
Shader "Formula9/CityBuilding"
{
    Properties
    {
        _Color ("Color", Color) = (1, 1, 1, 1)
        _WallHome ("Wall, homes", 2D) = "white" {}
        _WallOther ("Wall, other buildings", 2D) = "white" {}
        _WindowHome ("Windows, homes", 2D) = "white" {}
        _WindowOther ("Windows, other buildings", 2D) = "white" {}
        // What one copy of each texture covers in metres (width, height): homes in xy, others in zw
        _WallTileM ("Wall tile size (m): homes xy, others zw", Vector) = (2, 2, 1.1, 0.55)
        _WindowTileM ("Window tile size (m): homes xy, others zw", Vector) = (24, 24, 13, 13)
        // Below this the wall has no windows, like an HDB void deck or a shop front
        _GroundFloorM ("Plain wall height (m)", Float) = 3.5
        // How much the building's colour tints its windows; walls take the full colour
        _WindowTint ("Window tint", Range(0, 1)) = 0.35
        _Plain ("Plain colour only", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        CGPROGRAM
        #pragma surface surf Lambert fullforwardshadows vertex:vert
        #pragma target 3.0
        sampler2D _WallHome, _WallOther, _WindowHome, _WindowOther;
        fixed4 _Color;
        float4 _WallTileM, _WindowTileM;
        float _GroundFloorM, _WindowTint, _Plain;
        struct Input { fixed4 color : COLOR; float4 wall; };

        void vert(inout appdata_full v, out Input o)
        {
            UNITY_INITIALIZE_OUTPUT(Input, o);
            o.wall = v.texcoord;
        }

        // A little light of its own, so walls in shadow keep their colour instead of going black
        void surf(Input input, inout SurfaceOutput output)
        {
            fixed3 tint = _Color.rgb * input.color.rgb;
            float2 metres = input.wall.xy;
            float other = saturate(input.wall.z);
            // Every texture is sampled and then chosen between, which keeps mipmapping right at the
            // edge between the ground floor and the windows
            fixed3 wall = lerp(tex2D(_WallHome, metres / _WallTileM.xy).rgb, tex2D(_WallOther, metres / _WallTileM.zw).rgb, other);
            fixed3 windows = lerp(tex2D(_WindowHome, metres / _WindowTileM.xy).rgb, tex2D(_WindowOther, metres / _WindowTileM.zw).rgb, other);
            fixed3 textured = lerp(wall * tint, windows * lerp(fixed3(1, 1, 1), tint, _WindowTint), step(_GroundFloorM, metres.y));
            output.Albedo = lerp(textured, tint, saturate(max(input.wall.w, _Plain)));
            output.Emission = output.Albedo * 0.18;
        }
        ENDCG
    }
    FallBack "Diffuse"
}
