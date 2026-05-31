# Checkpoint Charlie's Cruise Missile (Alternate destination)

Alternate destination missile navigation mod with configurable cruise and terminal behavior.

## Features

- Alternate waypoint routing for cruise missiles.
- Midpoint smoothing between waypoints for less abrupt turns.
- Maximum bend angle enforcement to prevent sharp turns.
- Final lead-in waypoint generation toward target with optional random wobble.
- Deterministic spread offset so missiles aimed at the same point do not stack.
- Optional terminal jinking and top-attack popup behaviors.
- Optional flight path visualization.
- Configurable debug logging.

## Configuration options

All options are under the `General` section:

- `Cruise Altitude` (float, default `5`, range `3–15`) – Target radar altitude for cruise missiles in meters. Lower altitude increases the risk of terrain collision.
- `Minimum Altitude` (float, default `3`, range `1–3`) – Minimum radar altitude in meters before an emergency pullup.
- `Spread Radius` (float, default `15`) – Radius in meters to spread out missiles targeting the same location to prevent stacking.
- `Jinking maneuver in terminal approach` (bool, default `false`) – Enable random jinking during terminal approach.
- `Top attack popup maneuver in terminal approach` (bool, default `false`) – Enable top-attack popup maneuver during terminal approach.
- `Waypoint steps` (int, default `5`, range `1–20`) – Number of smoothing steps to apply on a waypoint.
- `Wobble activation distance` (float, default `5000`, range `0–50000`) – Enable random wobble when midpoint distance to target falls below this threshold.
- `Wobble range` (int, default `500`, range `0–5000`) – Random wobble offset range on X/Z while leading in (generated between -range and +range).
- `Max bend angle` (double, default `40`, range `0–180`) – Maximum angle between waypoints compared to a straight line in degrees. Everything over this will get smoothed out.
- `Display flight path` (bool, default `false`) – Display the flight path of the missile.
- `Debug logging` (bool, default `true`) – Enable debug logging output.

## Unit tests

Run from repository root:

`dotnet test AlteredDestination.Tests/AlteredDestination.Tests.csproj --configuration Release`
