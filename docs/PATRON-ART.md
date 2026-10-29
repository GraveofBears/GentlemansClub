# Making your own tavern patrons

How Emberville builds its NPCs, measured from the game's own files (Gumu, the store NPC in Town),
so your patrons will match the game's style and drop into the tavern without fuss.

## The sprite sheet

| Thing | What the game uses |
|---|---|
| Pixels per unit | **32** (one world unit = 32 px) |
| Frame size | about **33 × 33 px** per frame (Gumu: 33×33; leave a few px of padding around the character) |
| Pivot | **bottom centre, at the feet** (x ≈ 0.5, y = 0) |
| Filtering | **Point (no filter)**, no compression: crisp pixel art |
| Sheet layout | any grid; Gumu's is a 256 × 512 PNG with frames in rows |
| Shadow | a **separate small sprite** (a dark ellipse) on its own layer under the feet, not drawn into the character |

Look at `D:\EmbervilleRIP\ExportedProject\Assets\Texture2D\Gumu_Anim.png` (and `Adeos_Idle.png`, `Tinetia_Idle.png`) to see the style: dark outline, 3–4 shades per colour, top-down three-quarter view.

## Animations

The game's NPCs all share one animator, `_NPCBase_Ctrl`, and each character swaps in its own clips with an override controller (`Gumu_Ctrl.overrideController`). The clips it expects:

| Clip | Frames | Notes |
|---|---|---|
| Idle_Down | 6 | facing the camera; the one patrons will mostly use |
| Idle_Right | 6 | the game mirrors it for left |
| Idle_Up | 6 | back to the camera |
| Move_Down / Move_Right / Move_Up | 6 each | only needed if a patron walks |

Each frame lasts **0.125 s (8 frames per second)**, so a 6-frame idle loops every 0.75 s.

**For tavern patrons who stay put, the minimum is one `Idle_Down` loop (6 frames).** Nice extras, if you want them:
- `Idle_Right` so patrons can face each other across a table.
- A "drink" loop: raising a mug, 6–8 frames. I can wire extra clips to play now and then.
- A seated version (body cut off at the waist or legs bent), for patrons at tables. Sofas and stools in the tavern sheet will cover the lower body otherwise.

## Dancers (for the stage)

Same sheet rules as patrons. Suggestions:
- A dance loop of 8–12 frames at 8 fps (a 1–1.5 s loop) facing down/camera; a second loop to alternate with keeps it from looking mechanical.
- Keep costumes non-explicit (no nudity), in line with the game's art style; outfits, feathers, fans and lighting do the work.
- Stage lighting (coloured spotlights, a slow colour sweep) is done with the game's 2D lights, so don't paint glow into the sprite.

## What I need from you

- One PNG per patron (or one sheet with several), at 32 px per unit, frames in a grid.
- A note of which row is which animation, and the frame size if it isn't 33 × 33.
- A name for each patron and, if you like, two or three lines they might say.

I'll slice the sheets, make the clips and override controllers, add the shadow, the talk bubble and the dialogue trigger, and place them in the tavern.
