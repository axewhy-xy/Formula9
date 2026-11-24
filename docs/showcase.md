# Showcase session

A ready-made session that shows every feature built so far within about five minutes: route planning,
the 2D map and the 3D city, the interface and alert sounds, the incident and charging workflows, and
world events that happen on the road network on their own.

## Start it

1. Run `run.bat` once so the backend is set up, then close its backend window: both use port 8000.
2. Double-click **`showcase.bat`**. It prints the account to log in with and the event schedule. It
   uses in-memory accounts and a fresh temporary database, never the team's MongoDB, and forgets
   everything when its window closes.
3. Press Play in Unity (or open `Formula9.exe`). Log in with the account it printed (user
   `showcase`, password `showcase-demo`).

`showcase.bat --port 8001` serves elsewhere; then set `FORMULA9_BACKEND_URL=http://127.0.0.1:8001`
for the client.

## What happens, and when

Times are simulation seconds after **Start**. Pausing the simulation pauses the clock.

| Time | Event | Where | Shows |
| --- | --- | --- | --- |
| 0 s | Charge offer | shuttle-05 (starts at 17.8%) | Charging page: options, reserve forecast, **Apply decision** |
| 8 s | Pedestrian on the road (30 s) | ahead of shuttle-03 and shuttle-05 | A walker crosses where there is no crossing; traffic and shuttles stop for them |
| 20 s | Road closure (5 min) | ahead of shuttle-04 and shuttle-02 | Incident notification with sound, **Decide**, *Needs a decision*, detour options |
| 25 s | Signal fault (2 min) | junction-023, the next lights of shuttle-02 and shuttle-04 | Lights stuck on red in 3D; queues form behind the stop line |
| 50 s | Breakdown (5 min) | `return` roads, which every shuttle-01 detour rejoins | A vehicle pinned in its lane; traffic and the followed shuttle squeeze past |
| 90 s | Collision (3 min) | ahead of shuttle-03 and shuttle-05 | Two vehicles crashed nose to tail; the scene is kept clear |
| ~110 s on | Random events, every 45 to 90 s | anywhere on the network, mostly on shuttle routes | Accidents and closures nobody scheduled |
| ~5 min | Battery critical sound | shuttle-03's gauge turns red (below 20%) | Battery critical cue; a charge offer follows |
| ~6 min | Safety return | shuttle-05, if never sent to charge | The shuttle turns back to the depot on its own |

The schedule is `backend/fixtures/incidents/showcase.json`. Change the times or places there.
`backend/tests/test_showcase.py` checks that every event still stands on the exercise's roads and plays
out in front of the shuttles.

## Walkthrough

Each step lists the features it shows.

1. **Login.** The 3D backdrop is behind the login page. Log in. (*Login page, backdrop, Create account*)
2. **Create the session.** Select **Singapore, real streets (2D)** and click **Create training
   session**. (*Scenario picker, session list*)
3. **Route planning before the start.** Open **Route Planning**. The map shows the closure on
   shuttle-01's route over the OpenStreetMap basemap, with real bus stops when zoomed in. Click roads
   round the closure (map point sound), use **Undo**, **Redo** and **Clear**, or **Suggest a detour**.
   Each change is checked straight away; read the time, energy, distance and stop coverage.
   Enter a reason and **Approve route**. (*Routing, route editor, live checks, 2D map, map point and
   confirmation sounds*)
4. **Refused sound.** Before approving, press the greyed-out **Start and watch in 3D**.
   (*Refused sound*)
5. **Start.** Click **Start and watch in 3D**. The 3D view follows shuttle-01 through textured
   buildings, the drawn roads, traffic lights, background traffic and pedestrians. Right-drag orbits,
   middle-drag pans, the wheel zooms; **Overview** zooms out. Toggle **2D Map** / **3D View**.
   (*Start session sound, 3D city, traffic, pedestrians, camera*)
6. **Charging (0 s on).** Open **Charging**: shuttle-05 is low. Select a passing option, enter a
   reason and **Apply decision**. Watch it drive to the depot and charge. (*Charging workflow,
   depot trip, confirmation sound*)
7. **Pedestrian (8 s).** Select shuttle-05 or shuttle-03 in the fleet list and **Follow selected** to
   see the walker and the traffic stopping. (*Pedestrian event, 3D follow*)
8. **Road closure (20 s).** A notification appears over the map with the incident sound. Click
   **Decide**: Route Planning opens on that shuttle, and *Needs a decision* lists every shuttle
   waiting. Choose **Keep route**, **Hold at a stop**, **Request assistance** or **Re-route on the
   map**, give a reason and confirm. Options the backend doesn't offer are greyed out with the reason.
   (*Incident decisions, incident alert and confirmation sounds, notifications*)
9. **Signal fault (25 s).** Follow shuttle-04: its lights stay red and the traffic queues.
   (*Signal fault in 3D*)
10. **Breakdown (50 s).** Return to shuttle-01; when it reaches the `return` roads it passes the
    broken-down vehicle. (*Breakdown in 3D*)
11. **Collision and random events (90 s on).** Each new incident brings a notification and the
    incident sound. Decide some, leave others.
    (*Collision, world events that nobody scheduled*)
12. **Pause and resume.** **Pause simulation** and **Resume simulation**; decisions wait while
    paused. Resuming plays the start session sound. (*Session control, start session sound*)
13. **Battery critical (~5 min).** shuttle-03's gauge turns red below 20% and the battery critical
    sound plays once.
    Leave shuttle-05 alone instead of step 6 to see its safety return. (*Battery cue, safety return*)
14. **Mute alerts.** Toggle **Mute alerts**: alert sounds stop, interface sounds don't.
    (*Alert muting*)
15. **Reports.** Open **Reports**: the event log and commands with search and filters; **Incidents**
    lists every incident and its decision; **History** replays the simulation around one on a map;
    **Debrief** shows the rubric, decisions and outcomes. **What happened** on Route Planning reports
    stops served and each loop's time and energy. (*Reports, history replay, debrief, route outcome*)
16. **Assessment mode (optional).** **Assessment mode** creates the timed, graded exercise. It
    keeps its own incident plan, so the showcase schedule doesn't change it, and its grade appears
    under **Reports → Assessment**. (*Assessment, grading*)

## Sounds covered

| Sound | Step |
| --- | --- |
| Map point (a road picked for a route) | 3 |
| Start session (**Start and watch in 3D**, **Resume simulation**) | 5, 12 |
| Confirmation (a decision sent from **Approve route**, Route Planning's **Confirm** or **Apply decision**) | 3, 6, 8 |
| Refused (a greyed-out button pressed) | 4 |
| Incident alert | 8, 11 |
| Battery critical | 13 |

The disconnect, reconnect and session recovered sounds need the backend to drop and come back. The
showcase backend forgets its sessions when stopped, so use `run.bat`'s backend to show those.

## Notes

- shuttle-05 already starts below 20%, so it stays silent: the battery cue never plays for what was
  already true when the session was joined.
- How fast shuttle-03 drains depends on the traffic and on your decisions, so the battery cue can
  come a minute earlier or later.
- Random events use the session's own random numbers. The same seed gives the same events only if
  every decision is the same, so they differ between runs.
- Sessions tick in the showcase server's own process, not in worker processes, because spawned
  workers would not see the showcase schedule. One session runs well this way. Many at once do not.
