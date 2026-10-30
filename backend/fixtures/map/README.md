# Bundled static map

`map.db` and `map.osm` were copied unchanged from
`Formula_9/Native/Assets/Phase5_Map` at commit
`a5d699ec8c4536f7b52c248e4bd7140b1a415117`.

The backend reads the SQLite topology and enriches it from the adjacent OSM
file with direction, access and speed metadata. No Qt application or network
download is needed to use this map. `MAP_DATABASE_PATH` can select another
database with an adjacent `map.osm` file.

Map data: [OpenStreetMap and contributors](https://www.openstreetmap.org/copyright),
under the [Open Database License](https://opendatacommons.org/licenses/odbl/1-0/),
as attributed in the original `map.osm` header.
