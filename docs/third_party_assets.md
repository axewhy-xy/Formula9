# Third-party vehicle assets

The imported packs behind the background traffic (`Assets/Formula9/Resources/Formula9/TrafficVehicleCatalog.asset`). No licence file came with any of them, so each still needs its source and licence recorded by whoever imported it, and confirmed to allow redistribution among the team and in a submitted build.

| Folder under `Assets/` | Used for | Known origin | Source and licence |
| --- | --- | --- | --- |
| `Low poly vehicle pack lite` | Cars, one van | Not recorded | To confirm |
| `Cobra Games Studio/Low Poly Bus Pack` | Shuttle bus and one background van | Cobra Games Studio | [Low Poly Bus - Kozak I-VAN](https://assetstore.unity.com/packages/3d/vehicles/land/low-poly-bus-kozak-i-van-291276), Standard Unity Asset Store EULA |
| `Super Retro Compact Pack` | Taxi | Cheese Animal Productions (its `Readme.txt`), a purchased model | To confirm |
| `Ukraine_only_truck` | Trucks, heavy trucks, buses | Not recorded | To confirm |

The Kozak bus's original materials and `base.png` texture are included, with their material remaps restored. Shuttle bodywork takes its fleet colour; the windows, wheels and lights retain the pack's materials. The Ukraine pack's `all colors.png` texture is not in the repository, so those vehicles import with plain materials and take their colour from the traffic sample.

The project has no motorcycle asset; `CityTrafficRenderer` builds that shape itself.
