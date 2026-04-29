# Change Log

All notable changes to this project will be documented in this file. See [versionize](https://github.com/versionize/versionize) for commit guidelines.

<a name="1.6.2"></a>
## 1.6.2 (2026-04-30)

### Bug Fixes

* 7.5 changes

<a name="1.6.1"></a>
## 1.6.1 (2026-04-15)

### Bug Fixes

* handle 1pp being disabled for /mguide gpose

<a name="1.6.0"></a>
## 1.6.0 (2026-04-15)

### Features

* added chat command to toggle gpose handling

<a name="1.5.0"></a>
## 1.5.0 (2026-02-27)

### Features

* added option to track first person in gpose too

<a name="1.4.0"></a>
## 1.4.0 (2026-02-19)

### Features

* allow reverting to full motion in stationary emotes

### Bug Fixes

* track character rotation in reduced motion mode
* unlock remove camera roll setting to reduce confusion now that full motion can be partially enabled in reduced motion mode

<a name="1.3.6"></a>
## 1.3.6 (2026-02-11)

### Bug Fixes

* take dalamud UI scale into account in config window sizing

<a name="1.3.5"></a>
## 1.3.5 (2026-02-11)

### Bug Fixes

* hook specific camera instead of active camera, access CS types directly (thanks Haselnussbomber)
* limit slider widths and use helpmarkers for additional information

<a name="1.3.4"></a>
## 1.3.4 (2026-02-11)

### Bug Fixes

* add warning about 1st person camera auto-adjustment

<a name="1.3.3"></a>
## 1.3.3 (2026-02-05)

### Bug Fixes

* camera no longer spins when exiting NPC dialog in real first person
* camera staying rolled after entering mount from first person

<a name="1.3.2"></a>
## 1.3.2 (2026-01-27)

<a name="1.3.1"></a>
## 1.3.1 (2026-01-27)

### Bug Fixes

* camera snapping when looking up/down past the cam flip point
* handle mounted use
* movement controls past 90deg
* proper dirV clamping when upside down
* reimplement camera rotation with full quaternions

<a name="1.3.0"></a>
## 1.3.0 (2026-01-24)

### Features

* reduced motion in combat/instances

### Bug Fixes

* account for head pitch and yaw in relation to camera in determining appropriate camera tilt
* camera doing a spin when exiting first person in some poses
* shouldDrawGameObjectDetour to only use distance

<a name="1.2.9"></a>
## 1.2.9 (2026-01-10)

### Bug Fixes

* restore ability to scroll into first person

<a name="1.2.8"></a>
## 1.2.8 (2026-01-10)

### Bug Fixes

* reset camera properties after exiting first person

<a name="1.2.7"></a>
## 1.2.7 (2026-01-10)

### Bug Fixes

* camera no longer spins massively during transitions

<a name="1.2.6"></a>
## 1.2.6 (2026-01-10)

### Bug Fixes

* always show third person settings

<a name="1.2.5"></a>
## 1.2.5 (2026-01-09)

### Bug Fixes

* enable plugin flag should impact first person too

<a name="1.2.4"></a>
## 1.2.4 (2026-01-08)

### Bug Fixes

* forgot to add third person control toggle to config window..

<a name="1.2.3"></a>
## 1.2.3 (2026-01-08)

### Bug Fixes

* skip rotation adjustments during player control

<a name="1.2.2"></a>
## 1.2.2 (2026-01-08)

<a name="1.2.1"></a>
## 1.2.1 (2026-01-08)

<a name="1.2.0"></a>
## 1.2.0 (2026-01-08)

### Features

* add reduced motion toggle

<a name="1.1.6"></a>
## 1.1.6 (2026-01-08)

### Bug Fixes

* increase horizontal cam allowance from 90deg to 120deg

<a name="1.1.5"></a>
## 1.1.5 (2026-01-08)

### Bug Fixes

* finally correct rotational calculations for camera
* more robust attachment
* more transformation fixes..
* re-implement camera position offsets

<a name="1.1.4"></a>
## 1.1.4 (2026-01-08)

### Bug Fixes

* this time for real

<a name="1.1.3"></a>
## 1.1.3 (2026-01-08)

### Bug Fixes

* even more transformation fixes
* more first person cam fixes

<a name="1.1.2"></a>
## 1.1.2 (2026-01-07)

### Bug Fixes

* correct bone position transform while seated

<a name="1.1.1"></a>
## 1.1.1 (2026-01-07)

### Bug Fixes

* always draw player character when using first person mod
* one should not do math so late at night

<a name="1.1.0"></a>
## 1.1.0 (2026-01-07)

### Features

* more robust cam control, prep first person view

### Bug Fixes

* api14 in custom repository json
* euler math
* kinda working upside down too
* more horizontal rotation, handle simple heels offsets
* working first person view

<a name="1.0.8"></a>
## 1.0.8 (2025-12-19)

### Bug Fixes

* correct project sdk case

<a name="1.0.7"></a>
## 1.0.7 (2025-12-19)

<a name="1.0.6"></a>
## 1.0.6 (2025-12-18)

<a name="1.0.5"></a>
## 1.0.5 (2025-12-17)

<a name="1.0.4"></a>
## 1.0.4 (2025-09-10)

### Bug Fixes

* use combat distance in instances

<a name="1.0.3"></a>
## 1.0.3 (2025-08-07)

<a name="1.0.2"></a>
## 1.0.2 (2025-05-20)

### Bug Fixes

* status change race condition

<a name="1.0.1"></a>
## 1.0.1 (2025-04-26)

### Bug Fixes

* smoother cam distance change, respect enabled setting

<a name="1.0.0"></a>
## 1.0.0 (2025-04-26)

