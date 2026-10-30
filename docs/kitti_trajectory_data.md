# Understanding the KITTI Location & Trajectory Data

This document explains what the KITTI "oxts" data actually is, in plain
language, and how to get it. 

## The short version

Every KITTI drive comes with a recording of **where the car was, which way
it was facing, and how fast it was moving, once every tenth of a second.**
Play those recordings back in order, and you get the car's full trajectory
— its path through the world over time.

This is exactly the "ego vehicle position, orientation, speed, timestamp"
data our backend needs to hand off to both the C++ viewer and the web
viewer.

## It's frame-by-frame

This is the most important thing to understand: **there is no single file
that stores "the trajectory."** Instead, there's one small file *per
instant in time*, and the trajectory is just those files read in order.

Each drive has a folder like this:

```
oxts/
├── dataformat.txt     <- explains what's in each frame file (see below)
├── timestamps.txt     <- one timestamp per frame
└── data/
    ├── 0000000000.txt  <- frame 0
    ├── 0000000001.txt  <- frame 1
    ├── 0000000002.txt  <- frame 2
    └── ...              (one file per frame, in order)
```

Frame `0000000000` is the car's very first recorded instant. Frame
`0000000001` is a tenth of a second later. And so on. These frame numbers
line up exactly with the camera images and LiDAR scans for the same drive
— frame 42 of the location data is the *same moment in time* as frame 42
of the camera and frame 42 of the LiDAR.

## What's inside one frame

Each frame file is a single line of numbers. Here's a real one, from
`2011_09_26_drive_0001`, frame 0:

```
49.015003823272 8.4342971002335 116.43032836914 0.035752 0.00903 ...
```

On its own that's meaningless — but `dataformat.txt` tells you what each
number means, in order. Grouped into plain English:

**Where the car was**
| Field | Meaning | This frame's value |
|---|---|---|
| `lat` | Latitude of the car | 49.015003823272° |
| `lon` | Longitude of the car | 8.4342971002335° |
| `alt` | Altitude (height above sea level) | 116.43 m |

**Which way the car was facing**
| Field | Meaning | This frame's value |
|---|---|---|
| `roll` | Tilt side-to-side (0 = level) | 0.036 rad (~2°) |
| `pitch` | Tilt front-to-back (0 = level) | 0.009 rad (~0.5°) |
| `yaw` | Compass heading (0 = facing east) | -2.61 rad (~-149°, so facing roughly west) |

**How fast it was moving**
| Field | Meaning |
|---|---|
| `vn`, `ve` | Speed north / speed east |
| `vf`, `vl`, `vu` | Speed forward / left / up, relative to the car itself rather than the compass |

**How hard it was accelerating, and how fast it was turning**
| Field | Meaning |
|---|---|
| `ax`, `ay`, `az` | Acceleration in the car's own front/left/up directions |
| `af`, `al`, `au` | Same acceleration, but forward/leftward/upward-relative (used more often than ax/ay/az) |
| `wx`, `wy`, `wz` (and `wf`, `wl`, `wu`) | How fast the car was rotating around each axis — this is what tells you if it's turning, and how sharply |

**How trustworthy the GPS reading was**
| Field | Meaning |
|---|---|
| `pos_accuracy`, `vel_accuracy` | Estimated error in the position/velocity reading |
| `navstat` | Navigation status code |
| `numsats` | Number of GPS satellites the receiver could see |
| `posmode`, `velmode`, `orimode` | What GPS technique was used (plain GPS vs. more precise corrected modes) |

For our purposes — showing an ego vehicle moving and orienting itself
along a trajectory — the fields that actually matter most are **lat, lon,
alt** (position) and **roll, pitch, yaw** (orientation). The velocity and
acceleration fields are useful extras, but position + orientation per
frame is the core of what needs to reach the renderer.

## From raw GPS to a usable trajectory

Latitude/longitude aren't directly usable as 3D coordinates in a scene —
they need to be converted into a local, flat coordinate system first (a
small patch of Earth can be treated as flat without meaningful error).

The standard approach, and the one KITTI's own reference code
(`third_party/kitti_devkit/matlab/convertOxtsToPose.m`) uses:

1. Take the *first* frame's lat/lon as the origin — "the car started here."
2. Convert every other frame's lat/lon into a local x/y position relative
   to that origin (meters east/north of the start, roughly).
3. Combine that position with the roll/pitch/yaw to get a full pose
   (position + orientation) for each frame.

The result is a sequence of poses — exactly what a renderer needs to draw
a moving, correctly-oriented vehicle frame by frame.

## How to get this data

Run the retrieval script from the repo root:

```powershell
python scripts/download_kitti.py --drive 2011_09_26_drive_0001
```

This downloads and extracts the drive's data (including the `oxts/`
folder described above) into `data/kitti/`. This folder is gitignored —
everyone on the team runs the script themselves rather than pulling the
dataset through git.