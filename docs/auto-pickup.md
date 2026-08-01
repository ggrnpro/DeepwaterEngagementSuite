# Automatic loot pickup

Handover notes for whoever picks this up next. Everything described here is on `master` as of commit
`80df04a`, "Pick up loot without taking the mouse away from the player".

## What it does

Picks up loot on its own. It does not have a filter of its own and is not meant to grow one: the
player's in-game loot filter has already decided what is worth having, so the picker takes every
ground label the game still draws and clicks the nearest one. If the filter hides an item, no label
exists and the picker never sees it.

## The constraint that shaped it

Path of Exile 1 has no keyboard binding for picking an item up. The only way is a mouse click on the
label. The player asked, reasonably, not to have the pointer stolen out of their hand mid-fight, so
the whole design is about how to click without costing them control.

Three ways are implemented, chosen in settings:

| Mode | How | Pointer |
| --- | --- | --- |
| Borrow the cursor (default) | move to the label, click, move back to the exact pixel | away for 2-4 frames |
| Borrow only while the mouse is still | as above, but waits for the hand to stop moving | never seen |
| Post to the window | hands the hover and the click to the window handle | never moves at all |

The third one is what the player actually wanted, and it may simply not work: some clients ignore
posted mouse input and read the real cursor every frame instead. This is untested against a live
client. It fails safe - see "never click blind" below - so the worst case is that nothing gets picked
up, not that something goes wrong.

`PickItV2`'s answer to the same problem is a plugin bridge call into `MagicInput`, a plugin that is
not published anywhere on `exApiTools`. That route was not available.

## Never click blind

The single most important rule in the code: nothing is clicked until the game confirms the item is
under the cursor, via `Targetable.isTargeted` or the label's `HasShinyHighlight`. A click that misses
the label lands on open ground, and the character obediently runs there - which is exactly the
"I ran somewhere and died" outcome the player was worried about.

If the confirmation times out, the click is skipped, the item is left alone for half a second and
tried again. Only clicks that actually landed count against an item's attempt budget.

## Files

| File | Contents |
| --- | --- |
| `DeepwaterEngagementSuiteGGRN.AutoPickup.cs` | all of the logic, one partial of the main plugin class |
| `AutoPickupSettings.cs` | the settings tree, three classes |
| `NativeCursor.cs` | `GetCursorPos` and the `PostMessage` click |

Hooks into the existing plugin in three places:

- `Initialise()` calls `InitAutoPickup()` - registers the hotkeys and builds the portal label cache.
- `Render()` calls `RunAutoPickup()` **first**, before anything that returns early when the Deepwater
  handler is missing. Loot does not care whether this is a voyage.
- `AreaChange()` calls `ResetAutoPickup()` - clears the per-item caches.
- `DeepwaterEngagementSuiteSettings` gained `AutoPickupSettings`.

## How a pickup runs

`RunAutoPickup` is a plain per-frame call. It handles the toggle and pause keys, tracks the real
pointer, and then pumps a `SyncTask` through `TaskUtils.RunOrRestart`.

One iteration:

1. Respect the click interval.
2. `FindPickupCandidates()` - every visible ground label, filtered and sorted nearest first.
3. `ClickGroundLabelAsync()` on the nearest.
4. Record whether the click landed.

`FindPickupCandidates` drops a label when it is out of range, off the edge of the screen, on
cooldown, out of attempts, not actually an item, matched by an ignore pattern, unable to fit in the
bag, or sitting on top of a portal.

Two caches keep this cheap enough to run every frame:

- `_pickupItemFacts` - size, path, base name and stackability per ground entity, worked out once.
- `InventorySpace` - one snapshot of the bag grid per candidate sweep, not per candidate. A juiced
  map puts dozens of labels on screen.

## Borrowing the cursor, in order

```
save the real cursor position (GetCursorPos, not ExileCore's idea of it)
block the player's mouse                 (IInputManager.BlockUserMouseInput)
release the held move button             (only if they were holding it)
move to the label                        (re-read the rect - a running character drags labels)
wait for the game to confirm the target  (bounded, default 60ms)
click
hold still one frame                     (the click is queued, not consumed)
--- finally, always ---
move back to the saved pixel
press the move button again
unblock
```

The restore lives in a `finally`, and the task is never abandoned mid-borrow: `RunAutoPickup` keeps
pumping it even if the picker has been disabled or gated since, because only that task can give the
cursor back.

## The one edge case

If the player releases the left button during the ~40ms the cursor is borrowed, the game keeps
thinking the button is held and the character runs on its own. `GetAsyncKeyState` cannot tell a
physical button from a synthetic one, so this cannot be detected.

Mitigations: the pause hotkey (Space by default) and Escape both force a release, and
`Keep holding the move button through the click` can be turned off, which instead skips loot entirely
while the button is down.

## Other gates

Stops for: town and hideout, any fullscreen or large panel, the chat panel, the left or right panel,
Escape held, the window not being in the foreground, loading, a dead player, 150ms after the player's
own click, and optionally while a monster is close.

Note the "own click" timer fires on the *rising edge* only. Holding the left button to move must not
count as "the player is busy", or nothing would ever be picked up.

## Settings reference

Everything lives under `Automatic loot pickup`.

Top level: enable, toggle hotkey, hold-to-pause hotkey (Space), pickup range (600), milliseconds
between clicks (45), how long to wait for the item to light up (60), attempts per item (3), retry
cooldown (4s), and a debug overlay that outlines what would be taken.

`Cursor handling`: the window-message mode, the mouse-must-be-still mode and its threshold, whether
to restore a held move button, whether to block the player's mouse during the click, and the quiet
period after the player's own click.

`Safety`: pick up when the bag is full, pause while enemies are close and the range for it, avoid
portals, disable in town, disable with panels open, skip distant items while moving, and a comma
separated list of regexes matched against item path and base name.

## State

Builds clean. Never run against a live client. Worth checking first, in this order:

1. Turn on the debug overlay with the picker disabled and confirm the outlines match the loot filter.
2. Try the window-message mode. If items get taken, the pointer problem is solved outright.
3. Otherwise fall back to borrowing, and watch whether the blip is noticeable at the player's frame
   rate.

Nothing here reaches into the voyage, strongbox or trail code, and nothing in those touches input, so
the two lines of work do not overlap.
