# Checkpoint Charlie's Cruise Missile (Alternate destination)

Alternate destination missile navigation mod with configurable cruise and terminal behavior.

## Features

- Alternate waypoint routing for cruise missiles.
- Midpoint smoothing between waypoints for less abrupt turns.
- Final lead-in waypoint generation toward target with optional random wobble.
- Deterministic spread offset so missiles aimed at the same point do not stack.
- Optional terminal jinking and top-attack popup behaviors.
- Configurable debug logging.

## Configuration options

All options are under the `General` section:

- `Cruise Altitude` (float, default `5`)
- `Minimum Altitude` (float, default `3`)
- `Spread Radius` (float, default `15`)
- `Jinking maneuver in terminal approach` (bool, default `false`)
- `Top attack popup maneuver in terminal approach` (bool, default `false`)
- `Waypoint steps` (int, default `5`)
- `Wobble activation distance` (float, default `5000`)
- `Wobble range` (int, default `500`)
- `Debug logging` (bool, default `true`)

## Unit tests

Run from repository root:

`dotnet test AlteredDestination.Tests/AlteredDestination.Tests.csproj --configuration Release`
