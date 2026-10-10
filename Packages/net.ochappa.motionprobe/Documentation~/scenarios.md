# MotionProbe scenarios and options

A scenario is a short script. Commands are separated by `;` or newlines and fire at the current
time; `wait S` advances time. All times are game seconds on a fixed 60 fps step, so a scenario always
produces the same motion. A 1.5 s settling period (the "preroll") runs before t=0 and is not recorded.

```
MotionProbe.Run("<scenario>", "<options json>")      // execute_code, returns at once
motion_probe {"action":"run","scenario":"...","options":"{...}"}   // MCP custom tool
```

## Commands

| command | effect |
|---|---|
| `wait S` | advance S seconds (the run length is the sum of all waits) |
| `walk [V=1.6] [ACC=8]` | accelerate forward to V m/s. The avatar root really moves, so PhysBones feel the inertia. Sets VelocityZ |
| `run [V=4] [ACC=10]` | same, faster |
| `move VX VZ [ACC=8]` | target velocity in avatar-local m/s (x = right, z = forward) |
| `stop [DEC=14]` | decelerate to 0 |
| `turn DEG [SEC=0.35]` | yaw turn with ease in/out; + = clockwise seen from above. Adds the event `TURN END` |
| `jump [H=0.35]` | ballistic jump of H metres; sets Grounded false while airborne. Adds `LAND` |
| `shake DEG HZ SEC` | body yaw oscillation (default 30 deg, 2 Hz, 1.5 s). Adds `SHAKE END` |
| `sway CM HZ SEC` | sideways root oscillation (default 6 cm, 1.5 Hz, 2 s). Adds `SWAY END` |
| `bob CM HZ SEC` | vertical root oscillation (default 3 cm, 2 Hz, 1.5 s). Adds `BOB END` |
| `param NAME VALUE` | set an avatar parameter through Gesture Manager (bool = 0/1, true/false) |
| `gesture L R` | hand gestures 0-7 (GestureLeft / GestureRight) |
| `mark LABEL` | timeline marker (starts a new analysis segment) |
| `<preset>` or `preset <name>` | expands a preset and inserts `mark <PRESET>` |

Every command except `wait` becomes a timeline event. The analysis splits the run into segments at
events, so put a `wait` long enough to watch the response (1.5-3 s) after each stimulus.

## Presets

| preset | expansion | length |
|---|---|---|
| `idle` | `wait 3` | 3 s |
| `walk_stop` | `wait 0.3; walk 1.6; wait 1.5; stop; wait 2.5` | 4.3 s |
| `run_stop` | `wait 0.3; run 4; wait 1.2; stop; wait 2.5` | 4.0 s |
| `turn` | `wait 0.3; turn 180 0.35; wait 2.5` | 2.8 s |
| `jump` | `wait 0.3; jump 0.35; wait 2.5` | 2.8 s |
| `shake` | `wait 0.3; shake 30 2 1.5; wait 3` | 3.3 s |
| `sway` | `wait 0.3; sway 6 1.5 2; wait 3` | 3.3 s |
| `standard` | `wait 0.3; walk 1.6; wait 1.2; stop; wait 2; turn 180 0.35; wait 2; jump 0.35; wait 2; shake 30 2 1.2; wait 2.5` | 10 s |

Presets can be combined: `"walk_stop; jump"`.

## Options (JSON, all optional)

| key | default | notes |
|---|---|---|
| `avatar` | "" | avatar root name; needed when several avatars are in the scene (default: the one Gesture Manager controls) |
| `label` | preset name | suffix of the run folder; use e.g. "before" / "after" when tuning |
| `fps` | 60 | simulation and recording rate |
| `imageFps` | 30 | rendered frames per second (each view) |
| `preroll` | 1.5 | settling time before t=0 |
| `exitPlayMode` | true | leave play mode when done (ignored if play mode was already running) |
| `bodyViews` | "front,side,back" | any of front, back, side (= seen from the avatar's right), left, top |
| `headViews` | "front,side,back" | "" disables the head close-up |
| `bodyWidth` / `bodyHeight` | 300 / 400 | pixels per body view |
| `headSize` | 300 | pixels per head view |
| `sheetColumns` | 6 | key moments in the default sheets |
| `csv` | true | write data/*.csv |

## Recipes

- **General hair and cloth check:** `standard`.
- **One clean stimulus:** `walk_stop`, `run_stop`, `turn`, `jump`, `shake`, `sway`.
- **Strong spin:** `wait 0.3; turn 360 0.6; wait 3`.
- **Does it come to rest, or jitter while standing?** `idle` or `wait 5`. Anything moving at the end is flagged RESTLESS.
- **Dance, emote or toggle animation:** find the parameter name first (see below), then
  `wait 0.5; param DoodleDance 1; wait 4; param DoodleDance 0; wait 2`.
  If the animation loops, the report gives its period and writes sheet_cycle.png / trail_cycle.png.
- **Animation plus physics:** `param DanceX 1; wait 2; walk 1.6; wait 1; stop; wait 2; param DanceX 0; wait 2`.
- **Hand gestures:** `wait 0.3; gesture 2 2; wait 1.5; gesture 0 0; wait 1.5`.
- **Before/after tuning:** run the identical scenario string with labels `before` and `after`,
  then compare the PhysBone groups tables and `MotionProbe.Chain(run, "<group>")` of both runs.

## Finding avatar parameter names (execute_code)

```csharp
var root = GameObject.Find("Mafuyu");   // avatar root
var sb = new System.Text.StringBuilder();
foreach (var c in root.GetComponentsInChildren<Component>(true))
{
    if (c == null) continue;
    string tn = c.GetType().Name;
    if (tn == "VRCAvatarDescriptor")
    {
        var p = new SerializedObject(c).FindProperty("expressionParameters").objectReferenceValue;
        if (p != null)
        {
            var ps = new SerializedObject(p).FindProperty("parameters");
            for (int i = 0; i < ps.arraySize; i++) sb.Append(ps.GetArrayElementAtIndex(i).FindPropertyRelative("name").stringValue).Append(", ");
        }
    }
    else if (tn == "ModularAvatarParameters")
    {
        var ps = new SerializedObject(c).FindProperty("parameters");
        for (int i = 0; i < ps.arraySize; i++) sb.Append(ps.GetArrayElementAtIndex(i).FindPropertyRelative("nameOrPrefix").stringValue).Append(" [MA ").Append(c.gameObject.name).Append("], ");
    }
}
return sb.ToString();
```

Modular Avatar parameters only exist on the built avatar in play mode, which is what MotionProbe drives,
so `param` works for them too.
