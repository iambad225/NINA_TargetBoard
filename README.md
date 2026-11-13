# Target Board for N.I.N.A.

A calendar for your imaging targets, built like Google Calendar. It reads your NINA sequence files, shows each target's progress, and lets you plan targets onto nights by dragging them.

**It is read-only. It cannot change your sequences, and it does not run them.**

Built for NINA 3.2 (.NET 8). NINA 3.3 nightlies load it too.

## Read-only

Target Board looks at your sequences; it has no way to change them.

- **Sequence files.** It opens `.json`, `.xml` and `.ninaTargetSet` files only to read them. It never saves, edits, creates, renames, moves or deletes them.
- **Sequences open in NINA.** It does not add or remove targets, or change exposures, counts, filters or conditions in either of NINA's sequencers.
- **Running.** It does not load, start, stop or skip a sequence, and it does not touch your equipment. The calendar is a plan for you to follow; NINA does not act on it. You load and run your sequences in NINA as usual.
- **The board's own settings.** Conditions you set on a target, **Mark as done**, **Hide**, colors and notes live in the board's own files. They change what the board shows and plans, not the sequence.

From NINA it reads your profile's location, custom horizon and twilight times, the details of each light frame NINA saves, and which sequence file is running. It writes only its own files, in `Documents\N.I.N.A\TargetBoard` (see [Where the data lives](#where-the-data-lives)).

To change a sequence, edit and save it in NINA. The board reads the saved file again within a few seconds; **Re-read** does it at once.

## What it does

- **Reads your sequences.** Every target in your saved sequence files shows in a list, with its exposures per filter and how many are done. It reads Advanced Sequencer files and target templates (`.json`) and Simple Sequencer files (`.xml` targets and `.ninaTargetSet` target sets).
- **Plans by drag and drop.** Drag a target onto a night. Night and Week views have one column per night, with dusk at the top and dawn at the bottom, so you drop it at the time you want. Month view is a grid of nights.
- **Keeps to dark time.** An event can only sit inside dark time (Sun below -18°, or -12° if you choose nautical).
- **Clips by conditions.** Each target has conditions such as *altitude at least 30°*, *above my custom horizon*, *hour angle between -3h and +3h* or *local time between 22:00 and 02:00*. The calendar shades the times a selected target meets all of them, and events stay inside those times.
- **Overflow goes to another night.** If a block doesn't fit before dawn or the target's limits, the part that fits is placed. The rest stays on the target as "to place", for you to drag onto another night.
- **Never plans more than you need.** A target's events can add up to what its sequence still needs, plus overhead, and no more.
- **Checks what happened.** Each past event shows as done, partly done or missed, from the frames NINA logged. The next morning the board fits the plan to what was done.
- **Shows the filters.** Double-click an event to see which frames it will take, or took.
- **Groups target sets.** The targets of a `.ninaTargetSet` sit under one row. Drag that row onto a night to plan the whole set in order.
- **Tracks progress.** Progress comes from the sequence's own loop counters, NINA's per-target exposure counts, and every light frame NINA saves while the plugin runs.
- **Keeps history.** It records the date each filter line was finished (for example *Ha 300s complete 20/20*) and the date the whole target was finished. Finished work shows on the calendar as "done" sessions on the night it happened.

## Install

### From NINA (gets updates)

1. In NINA, open **Options › General**. Under **Plugin Repositories**, press **+** and add:

   ```
   https://raw.githubusercontent.com/iambad225/NINA_TargetBoard/nina-repo
   ```

2. Open the **Plugins** tab, choose **Available**, find **Target Board** and install it. Restart NINA when it asks.
3. Go to the **Imaging** tab, open the panel picker and turn on **Target Board**. Drag the panel out to float it, or dock it somewhere large.

When a new version comes out, the **Plugins** tab shows a count of updates, and Target Board offers **Update**.

### From source

You need Windows, Visual Studio 2022 (17.8 or later) or the .NET 8 SDK, and NINA 3.2 or later.

1. Close NINA.
2. Open `TargetBoard.sln` and build the **TargetBoard** project (Debug or Release). Or run `dotnet build src/TargetBoard -c Release`.
3. The build copies `TargetBoard.dll` to `%LOCALAPPDATA%\NINA\Plugins\3.0.0\Target Board\`. NINA 3.x reads plugins from that folder.
4. Start NINA. Go to the **Imaging** tab, open the panel picker and turn on **Target Board**. Drag the panel out to float it, or dock it somewhere large.

The plugin's settings are on the gear button in the panel header, and under **Options › Plugins › Target Board**. Both start with a short list of tips, which also appears on NINA's plugin page.

The target list runs down the left side of the panel and the calendar fills the right. Double-click a row to open its details in the lower half of the list (Esc or ✕ closes them). Drag the bars between the parts to resize them; the board remembers the sizes.

## Using it

**Target list (left).** Each row shows the target's color, progress bar, exposure time left, how much time is still *to place* on the calendar, and the file it came from with the date that file was created. Click a target to select it; the calendar then shades the times it can be shot. Double-click it (or right-click → **View details**) to see its exposures, edit its conditions, change its color, hide it, or read its completion history.

- **Sort by** orders the list by the date the file was created (newest or oldest first), the date it was last saved, name, progress, or time left to place.
- **Target sets** show as one row with their targets nested under it, in the set's order. Click the arrow to fold a set away. Click the set's row to see all its targets together.
- **Copies.** If a target from a set is also saved as its own `.xml` (same name, same pointing within 1 arcminute), the copy folds into the set's row as *Also saved as …*, with its own progress. Turn on **Show copies** to list copies as rows of their own.
- **Show hidden** and **Show finished** bring back targets you hid and targets that are done.
- **Right-click** a target for **Mark as done** and **Hide**. Right-click a set's row to do either for every target in the set.
- Click a selected row again to deselect it.
- The mouse wheel speed and smooth scrolling are under **Target list** in the settings. 1× is Windows' usual step of three lines; lower is slower.

**Planning.**
- Drag a target from the list onto a night. In Night and Week views it starts where you drop it; in Month view it takes the first free slot that night.
- Drag a target set's row to plan its targets back to back, in order. Finished targets and targets already fully planned are skipped. Targets that don't fit before dawn stay *to place*.
- The **Drop** box sets how much time a drop adds: everything left to place (with overhead), or a fixed 30 minutes to 4 hours.
- A target's events can't add up to more than its sequence still needs, plus overhead. A drop, move or resize that would go past that stops at the limit. To plan a fully planned target on another night, shorten or remove one of its events first.
- With a target selected, double-click an empty time to place it there.
- Drag an event to move it, even to another night. Drag its bottom edge to change its length.
- Right-click an event to remove it, split it in two, or select its target. Press **Delete** to remove the selected event.
- While you drag, the outline turns green if it fits, amber if part of it overflows, and red if it can't go there. The status line says why.
- Hold **Shift** while you drop, move or resize to ignore the target's conditions (altitude, custom horizon, hour angle, time window). Dark time and the horizon (0°) still apply. These events get a dashed outline and a ⚠ triangle, and the morning settle never moves them.
- **Ctrl+click** selects several events. Drag any one of them to move them all: each moves by the same nights and clock offset and lands as a single event would (at its spot if free, otherwise the nearest free slot on its night, shortened if needed). Events you did not select never move. **Delete** removes the selected events, **Ctrl+→** / **Ctrl+←** move them a night, right-click offers the same. **Ctrl+Z** (or the **Undo** button) takes back the last change to the plan.
- **Double-click** an event to see its frames, in the order the sequence shoots them. Esc closes the view.

**Reading the calendar.**
- Hatched areas are twilight and daylight. The dark part of each column is the night you can use.
- The tinted band and colored edge show when the selected target meets its conditions. A curve shows its altitude (0° at the left edge of the column, 90° at the right). A dot marks its highest point: Night view gives the altitude and the time, Week view the altitude.
- The thin strip on the right of each column shows done sessions from the frame log. Circles mark milestones (✓ a filter finished, ★ a target finished).
- The red line is now. Past parts of the night are dimmed.
- The mouse wheel moves the calendar a night at a time (down goes ahead, up goes back); Week view slides its seven nights. In Month view the wheel moves a month. **Tonight** and the date picker go back to a calendar week; ◀ ▶ move a whole week.
- Each column header shows the Moon's illumination, the hours of darkness and the time already planned, and ⚠ when an event was missed.
- Past events are drawn faded, marked ✓ done, ◐ partly done or ✗ missed.
- The **Moon** switch in the toolbar draws the Moon's altitude as a dotted grey line, with its phase at the highest point.

**The morning settle.** Nothing on the plan changes while a night is under way. At the settle time the next morning (08:00 unless you change it under *Each morning* in the settings; never before dawn) the board settles last night once:

1. Each event becomes a record: done, partly done or missed.
2. A partly done event shrinks to the time its frames took. Its planned block stays as a faint dashed outline.
3. The missing time goes onto that target's next event, which grows up to the end of its slot.
4. Events in the way move later on their night, or shorten to fit it. One goes to a later night only when its own night has less than the shortest event length left. Time cut off goes back to "to place".
5. Events placed outside their conditions with Shift stay where they are.

Missed events are only marked. Right-click one, or the night's header, and choose **Shift the plan one night later** (that night's missed events and everything after move a night, same clock time) or **Add the missed time to each target's next event**. Or drag the missed event onto another night: its time is planned again there and the missed event stays as a record. To settle a night before the settle time, right-click its header once dawn has passed. Ctrl+Z undoes any of these.

The settle relies on the frame log. If **Log every light frame** is off, past events show as unknown and nothing is settled.

**Marking a target done.** Use this when you have enough data, or have given up on a target, but its sequence still has frames to take. The board then counts it as finished: it has nothing left to place, it drops out of the list unless **Show finished** is on (its details, if open, stay open until you close them), and its history records the date you marked it. Its real progress still shows. Events you already planned stay on the calendar until you remove them. The sequence file is not changed. **Mark as not done** undoes it, and that is recorded too.

**Conditions.** New targets get the defaults from the settings: altitude at least 30° and above your custom horizon. If the sequence has *Loop until altitude*, *Above horizon*, *Wait for altitude* or *Wait until above horizon*, those are copied too. Change them per target in the details panel; this changes only the board's copy, not the sequence. **Reset** puts back the defaults plus the sequence's own conditions.

## How progress is read

| Source | What it gives | When it updates |
|---|---|---|
| Loop counters (`Iterations`, `CompletedIterations`) on Smart Exposure, Take Many Exposures and any looping container | Planned and done frames per filter and exposure | When NINA saves the sequence file |
| `ExposureInfoList` on each target container | NINA's own count of light frames taken | When NINA saves the sequence file |
| Simple Sequencer rows (`TotalExposureCount`, `ProgressExposureCount`) in `.xml` and `.ninaTargetSet` files | Planned and done frames per filter and exposure | When NINA saves the file |
| Light frames NINA saves | Exact capture times, live progress, "done" sessions | Right away |

NINA writes counters to disk only when it saves the sequence. To keep the board current between nights, add NINA's **Save Sequence** instruction at the end of each target, or save the sequence after a session. Frames logged live count on top of the saved counters until the next save.

A target that loops until a time or altitude (not a count) is marked **Open-ended loop**. Its total is one pass through its exposures.

## Where the data lives

Everything is in `Documents\N.I.N.A\TargetBoard`, next to NINA's own `Targets` and `Templates` folders:

| File | Contents |
|---|---|
| `board.json` | Settings, target colors, conditions and notes, and your planned events |
| `history.json` | Progress snapshots and completion milestones with dates |
| `captures.jsonl` | One line per light frame NINA saved (time, target, filter, exposure, and the file NINA was running) |
| `undo.json` | The last 30 changes to the plan, so Ctrl+Z still works after NINA restarts |

Files are written to a temporary copy first and then swapped in, and the last good copy is kept as `.bak`. If Windows moves your Documents folder (for example to OneDrive), the board follows it.

## Notes and limits

- NINA does not store a creation date inside its files, so **Date created** uses the date Windows gives the file. Copying a file from another computer or restoring it from a backup can reset that date.
- A target saved as its own `.xml` and also inside a set counts as two targets, each with its own progress. Each frame goes to the file NINA was running when it saved the frame. When NINA can't say, the frame goes to the copy inside the set. Plan the one you shoot from.
- The same target in two standalone files (say `M 20.xml` and `M 20 HaLRGB.xml`) shows twice. The file name under each row tells them apart; hide the one you don't use.
- A target's identity is its file path plus its name. If you rename or move a sequence file, its targets look new; events for the old ones stay on the board marked *File not found*. Remove them or leave them as a record.
- Dark times come from NINA's own twilight calculation for your profile's location. Target altitudes use the same formula as NINA's altitude charts (no refraction), computed in the plugin so a month of nights redraws quickly. Unit tests check it against astropy to within 0.05°.
- Times on the calendar are your computer's local time. On the night clocks change, that night's column shows the hour twice or skips it.
- Events can't overlap by default (one telescope). Turn on **Events may overlap** if you plan several rigs on one board.
- The frame forecast assumes every frame costs its exposure plus the overhead percentage, and only whole frames fit. Real nights vary.
- Undo keeps the last 30 changes, and they survive a restart.
- Template files (`*.template.json`) are skipped.

## Making a release

Raise `AssemblyVersion` (and `AssemblyFileVersion`) in `src/TargetBoard/Properties/AssemblyInfo.cs`, add a section for that version at the top of `CHANGELOG.md`, and push to `main`. The workflow in `.github/workflows/release.yml` then:

1. runs the tests and builds the plugin on Windows;
2. packs `TargetBoard.dll` into `TargetBoard-<version>.zip` and publishes it as release `v<version>`, with the changelog section as its notes;
3. adds the version to the `nina-repo` branch, which NINA reads as a plugin repository.

A push that doesn't change the version only builds and tests. `build/release.py` does the packing; it checks the plugin details against NINA's manifest rules and stops the release if they fail.

## Project layout

```
TargetBoard.sln
build/release.py             Packs a release and writes the manifest NINA's plugin manager reads
.github/workflows/           Build, test and release on every push to main
src/TargetBoard.Core/        Plain .NET 8 library: sequence reader, sky math, placement rules, history, storage
src/TargetBoard/             The NINA plugin (WPF). Compiles the Core sources into one DLL.
  TargetBoardPlugin.cs       Plugin entry point and options page
  BoardContext.cs            Shared board: file scanning and watching, NINA events, frame logging
  Nina/NinaSkyContext.cs     Location, custom horizon and twilight from NINA
  Board/                     Dock panel, view model, calendar control, views
  Settings/                  Settings view and view model
  Templates.xaml             Panel and options page templates
tests/TargetBoard.Core.Tests Console test runner for the core (dotnet run -c Release)
```

Run the tests with `dotnet run -c Release --project tests/TargetBoard.Core.Tests`. They cover the sequence reader (NINA 3.2 and 3.3 file formats), sky positions and twilight against astropy, visibility windows, placement and overflow, milestones, storage recovery and file scanning.

## License

MPL-2.0, the same as NINA. See `LICENSE`.
