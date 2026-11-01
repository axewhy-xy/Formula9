// The extruded city: walls carry a wall texture on the ground floor and window texture above it, both
// tiled by their real size in metres and tinted by the building's colour; roofs stay plain colour.
// CityMesh gives each vertex (metres along the wall, metres up, texture set, 1 on a roof) in its first
// texture coordinate. The sets are 0 homes (private flats), 1 every other building, 2 HDB blocks,
// 3 low-rise homes and 4 offices. _Plain turns the textures off, for the shuttle, stop and depot
// markers that share this material.
Shader "Formula9/CityBuilding"
{
    Properties
    {
        _Color ("Color", Color) = (1, 1, 1, 1)
        _WallHome ("Wall, homes", 2D) = "white" {}
        _WallOther ("Wall, other buildings", 2D) = "white" {}
        _WallHdb ("Wall, HDB blocks", 2D) = "white" {}
        _WallLowRise ("Wall, low-rise homes", 2D) = "white" {}
        _WallOffice ("Wall, offices", 2D) = "white" {}
        _WindowHome ("Windows, homes", 2D) = "white" {}
        _WindowOther ("Windows, other buildings", 2D) = "white" {}
        _WindowHdb ("Windows, HDB blocks", 2D) = "white" {}
        _WindowLowRise ("Windows, low-rise homes", 2D) = "white" {}
        _WindowOffice ("Windows, offices", 2D) = "white" {}
        // What one copy of each texture covers in metres: the wall's (width, height) in xy, the
        // windows' in zw
        _TileHome ("Tile size (m), homes: wall xy, windows zw", Vector) = (2, 2, 24, 24)
        _TileOther ("Tile size (m), other buildings: wall xy, windows zw", Vector) = (1.1, 0.55, 13, 13)
        _TileHdb ("Tile size (m), HDB blocks: wall xy, windows zw", Vector) = (2, 2, 9, 6)
        _TileLowRise ("Tile size (m), low-rise homes: wall xy, windows zw", Vector) = (1.5, 1.5, 18, 21)
        _TileOffice ("Tile size (m), offices: wall xy, windows zw", Vector) = (3, 3, 30, 30)
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
        sampler2D _WallHome, _WallOther, _WallHdb, _WallLowRise, _WallOffice;
        sampler2D _WindowHome, _WindowOther, _WindowHdb, _WindowLowRise, _WindowOffice;
        fixed4 _Color;
        float4 _TileHome, _TileOther, _TileHdb, _TileLowRise, _TileOffice;
        float _GroundFloorM, _WindowTint, _Plain;
        struct Input { fixed4 color : COLOR; float4 wall; };

        void vert(inout appdata_full v, out Input o)
        {
            UNITY_INITIALIZE_OUTPUT(Input, o);
            o.wall = v.texcoord;
        }

        // 1 for the building's own set and 0 for the others; a building's set is the same at every vertex
        float SetWeight(float set, float index) { return saturate(1 - abs(set - index)); }

        // A little light of its own, so walls in shadow keep their colour instead of going black
        void surf(Input input, inout SurfaceOutput output)
        {
            fixed3 tint = _Color.rgb * input.color.rgb;
            float2 metres = input.wall.xy;
            float set = input.wall.z;
            float4 w = float4(SetWeight(set, 0), SetWeight(set, 1), SetWeight(set, 2), SetWeight(set, 3));
            float office = SetWeight(set, 4);
            // Every texture is sampled and then chosen between, which keeps mipmapping right at the
            // edge between the ground floor and the windows
            fixed3 wall = tex2D(_WallHome, metres / _TileHome.xy).rgb * w.x + tex2D(_WallOther, metres / _TileOther.xy).rgb * w.y
                + tex2D(_WallHdb, metres / _TileHdb.xy).rgb * w.z + tex2D(_WallLowRise, metres / _TileLowRise.xy).rgb * w.w
                + tex2D(_WallOffice, metres / _TileOffice.xy).rgb * office;
            fixed3 windows = tex2D(_WindowHome, metres / _TileHome.zw).rgb * w.x + tex2D(_WindowOther, metres / _TileOther.zw).rgb * w.y
                + tex2D(_WindowHdb, metres / _TileHdb.zw).rgb * w.z + tex2D(_WindowLowRise, metres / _TileLowRise.zw).rgb * w.w
                + tex2D(_WindowOffice, metres / _TileOffice.zw).rgb * office;
            fixed3 textured = lerp(wall * tint, windows * lerp(fixed3(1, 1, 1), tint, _WindowTint), step(_GroundFloorM, metres.y));
            output.Albedo = lerp(textured, tint, saturate(max(input.wall.w, _Plain)));
            output.Emission = output.Albedo * 0.18;
        }
        ENDCG
    }
    FallBack "Diffuse"
}
