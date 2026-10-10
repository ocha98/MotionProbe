# Reading MotionProbe results

Everything is in `<project>/MotionProbeRuns/<yyyyMMdd_HHmmss>_<label>/`. `MotionProbeRuns/latest.txt`
holds the newest run folder. Unity does not import these folders.

## Run folder

| file | content |
|---|---|
| `status.json` | `state` (queued, starting, preroll, running, analyzing, done, error, aborted), `frame`/`frames`, `simTime`, `message` (headline or error), `outputs` |
| `report.md` | the main result. Read it first and completely |
| `sheet_body.png` | rows = views (front / side = seen from the avatar's right, avatar faces right / back), columns = key moments: start, the largest PhysBone motion in each segment, end. The header gives the time and event context such as `STOP+0.13` |
| `sheet_head.png` | the same moments as a head close-up (hair, ears, face) |
| `trail_physbone.png` | the 6 most active PhysBone groups, each as a zoomed front and side panel. White = rest shape at t=0, orange = shape at the group's peak, coloured line = tip path over time (blue -> green -> yellow -> red), bar = scale. Body motion is removed: paths are relative to the chain's parent bone |
| `trail_body.png` | full body: paths of head, hips, hands and feet relative to the avatar root. Dots every 0.1 s, ring = start, square = end |
| `trail_cycle.png` | only when a loop is detected: zoomed paths per limb over exactly one period. Numbered dots = phases 0-7, matching the columns of `sheet_cycle.png` |
| `sheet_cycle.png` | only when a loop is detected: front and side views at 8 phases of one period |
| `chart.png` | top: displacement over time (max per group) for the 8 most active groups; bottom: root speed and limb speeds; dashed lines = events |
| `data/chains.csv` | one row per chain: rest length, peak, angle, settle/frequency/decay after the last event, jitter, stretch, collider clearance |
| `data/timeseries_cm.csv` | tip displacement (cm) of every chain at 30 Hz |
| `data/body.csv` | root position/yaw and limb positions relative to the root (cm) at 30 Hz |
| `data/meta.json`, `data/frames.bin` | raw recording (all tracked points every frame) used by the drill-down calls and `MotionProbe.Rebuild` |
| `frames/<group>_<view>/<frame>.jpg` | cached renders at 30 fps, used by `Sheet` / `SheetAt` / `Trail` |

## Conventions

- **Time** is in seconds from t=0, the settled rest pose after the preroll. Events come from the scenario.
  Automatic events (`TURN END`, `LAND`, `SHAKE END`, `SWAY END`, `BOB END`) mark when a stimulus finished,
  so the segment that follows is the free response.
- **Context** such as `LAND+0.13` means 0.13 s after the most recent event.
- **Body axes:** x = avatar's right, y = up, z = avatar's forward. Distances are in cm unless noted.
- **Segments** start at each event. Per-segment numbers describe what happens until the next event.
- **Cameras** are orthographic and locked to the avatar root's yaw and horizontal position. They do not follow its height.
  The front view therefore keeps showing the face during turns, travel is cancelled out, and jumps stay visible.
  World lighting does not rotate with the avatar, so shading changes during turns.

## PhysBone numbers

- **Chain:** one path from a PhysBone root to one leaf, plus the endpoint if one is set. A component with several
  branches gives several chains (`Component/LeafName`).
- **Group:** components whose names differ only by number or side (HairB1..14, HairB8.L/.R), merged.
- **Displacement (peak, cm):** tip position minus where the tip would be if the whole chain followed its parent bone
  rigidly in the t=0 pose. Body animation is cancelled out; what remains is physics.
  `%len` = displacement / chain length. `max deg` = angle between the actual and the rest direction root -> tip.
- **hangs under gravity:** the chain has gravity and points within 30 deg of straight down at its peak. A large
  displacement there means the parent bone rotated (for example an arm was raised) while the chain kept hanging.
  That is correct physics, not flailing.
- **settle:** seconds from the segment start until the tip stays within max(0.3 cm, 10% of the segment peak) until
  the segment ends. `not settled` / `no (k/n)` = it never got there before the next event.
- **swings:** turning points from the peak on with amplitude >= max(0.3 cm, 15% of peak). 1-3 is typical for hair and cloth.
- **freq (rod):** measured swing frequency from the turning points. `rod` is the natural frequency of a uniform rod of
  the same length hanging from one end, a physical reference. Much higher than rod reads springy or stiff;
  much lower reads floaty or slow-motion.
- **decay/half-swing:** median amplitude ratio between consecutive half swings after the peak. Below 0.3 is heavily
  damped (stops almost immediately); 0.4-0.7 is natural-looking; near or above 1 means it is still being driven or under-damped.
- **main axis:** dominant direction of the swing in body axes (fwd/back, side, up/down).

## Warnings and info

| tag | condition | usual meaning |
|---|---|---|
| JITTER | for at least 0.25 s the acceleration reverses on more than 45% of frames, with more than 0.5 mm amplitude | buzzing or vibrating, almost always looks broken (collider fights, conflicting limits) |
| PENETRATION | more than 0.5 cm inside a collider assigned to the chain | collider too small, bone radius too small, or the chain is forced through |
| INTRUSION | more than 1 cm inside an avatar collider that is NOT assigned to the chain (and was clear of it at t=0) | likely visible clipping into the body; consider assigning that collider |
| STRETCH | chain length outside 85-115% of rest | intended only if maxStretch/maxSquish are set |
| NOT_SETTLED | still more than 25% of its last peak (and more than 0.5 cm) off rest at the end while the body is still | slow return, sticky, or held by immobile/limits |
| RESTLESS | still moving faster than 1 cm/s in the last 0.5 s while the body is still | keeps wobbling: low pull, high spring/momentum |
| ASYMMETRY | left/right counterparts (matched by name) differ by more than 40% in peak | fine if the outfit or motion is asymmetric; otherwise a setting differs |
| LARGE_SWING (info) | more than 60% of length or more than 70 deg | check visually; often fine after a big stimulus |
| BOUNCY (info) | 6 or more swings after the last event | under-damped |
| BARELY_MOVES (info) | longer than 8 cm but under 0.3 cm of motion although the root moved | very stiff or immobile; fine for accessories that should not swing |

Collision checks use only VRChat PhysBone colliders (sphere, capsule, plane). The chain is sampled at its joints and
segment midpoints with the PhysBone radius. Mesh-to-mesh clipping is not computed, so inspect the sheets for that.

## Body motion

- **limb RMS whole / steady:** spread of head/hips/hand/foot positions relative to the root within the segment.
  "Steady" skips the first 0.6 s of the segment, i.e. the transition into the new motion.
- **loop period:** found by autocorrelation of limb velocities over the steady part (r >= 0.7). For an animation
  clip it usually equals the clip length.
- **largest ranges:** max - min of limb positions per axis (cm), for the whole segment and for the steady part.
- **Pose samples:** limb positions relative to the root at the key moments shown in the sheets.
- **root:** distance travelled, max speed, max yaw change and max rise from the scenario's root motion.

## Rules of thumb for "natural" (heuristics, confirm with the images)

- Hair and cloth lag the body, overshoot once or twice, and settle within roughly 0.5-2 s after a stop.
- Swing frequency in the same range as the rod reference (0.5x-2x).
- No jitter, no visible clipping, nothing that keeps moving when the body is still.
- Small accessories (ribbons, chains) can be livelier; heavy items (tails, long hair) slower.
- Taste is the user's call. Give measurements, say why something may look off, and let them decide.

## Known limitations

- VRChat `proxy_*` locomotion clips are single-frame placeholders outside the VRChat client: during walk/run the
  legs hold one stride pose. Root movement and inertia are real; leg-driven effects (a skirt kicked by the legs) are
  missing. The report adds a note when this applies.
- Avatar parameters need Gesture Manager. Without it they are set on the Animator directly and may do nothing.
- Only one run at a time. Do not change play mode, the scene or the avatar during a run.
- PhysBone collision with other players, grabbing and posing are not simulated.
