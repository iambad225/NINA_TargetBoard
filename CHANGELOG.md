# Changelog

## 1.4.0.0

- **Details stay open.** Marking a target done or hiding it from its details no longer closes them, though its row leaves the list. Close them with ✕ or Esc. If the target comes back to the list, it is still selected.
- **Undo survives a restart.** The board saves the last 30 changes in `undo.json`, next to `board.json`, so Ctrl+Z still works after NINA restarts.
- **The board remembers panel sizes.** It keeps the list's width and the split between list and details when NINA restarts.
- **Frames go to the file you are running.** When a target is in a target set and also saved as its own `.xml`, each frame now goes to the one NINA was running: the `.xml` if you loaded it in the Simple Sequencer, the set if you loaded the set, or the Advanced Sequencer's `.json`. The board reads this from NINA as each frame is saved and changes nothing in NINA. When NINA can't say, the frame goes to the copy inside the set, as before. Frames logged earlier stay where they were.

## 1.3.1.0

- **Show finished** is off when the board opens, so finished targets stay out of the list until you turn it on. Picking a finished target's event on the calendar still turns it on, so its row can be shown.

## 1.3.0.0

The board's mouse and keyboard handling is rewritten, rather than patched again.

- **The target list is now a plain list of rows**, not a WPF ListBox. A ListBox selects, scrolls and holds on to the mouse by itself, and those habits clashed with the board's own clicks, double-clicks and drags: a wheel turn over the calendar could scroll the list, and clicks in the details could select a target. Now nothing in the list acts on its own.
- **The selection lives in one place** (the board's view model); rows only draw it. Only these change it: a click, double-click or right-click on a row, a click on an event in the calendar, a menu item, or a row leaving the list.
- **One handler owns every press on a row**: release = click (a click on the selected row deselects it once the double-click time has passed), second press = double-click (opens the details), move = drag onto the calendar, right-click = select and open the menu. The set's arrow button keeps its own click.
- **Nothing holds the mouse** except a drag in progress, which the system releases when it ends. The calendar holds it only while you press an event, and lets go on release, Esc, double-click or any error.
- **The mouse wheel goes only to what is under the mouse**: the list, the details or the calendar.
- **Keys:** Esc closes the details (unless the calendar has the keyboard, where Esc keeps its calendar meaning). Ctrl+Z now undoes from anywhere on the board, except while typing in a text box.
- A target picked on the calendar scrolls into view in the list.

## 1.2.2.0

- Fixed: clicking a button near the target list could select a target instead. The list could keep hold of the mouse after a click or a drag and then took every later click, picking whichever row was nearest. It now lets go as soon as the button is up.
- The labels next to the switches (Show hidden, Show finished, Show copies, Moon, Mark as done, Hide from the list) now toggle them too.
- **View details** in the right-click menu of a target or target set.
- The mouse wheel over the calendar moves it a night at a time: down goes ahead, up goes back. Week view now shows seven nights from its first night, so it can slide a night at a time; Tonight and the date picker still start it on a calendar week, and ◀ ▶ still move a week. In Month view the wheel moves a month.

## 1.2.1.0

- **Layout.** The target list runs the full height of the left side again, with the calendar to its right. The details of a target or target set open in the lower half of the list only when you double-click its row; ✕ or Esc closes them, and the split between list and details is remembered while NINA runs. A single click still selects, so the calendar shades the times the target can be shot.
- A second click on a selected row still deselects it, but waits out the double-click time, so a double-click opens the details instead.

## 1.2.0.0

- **Drag several events together.** Ctrl+click to select events, then drag any one of them: they all move by the same number of nights and the same clock offset, so the gaps between them stay the same. Each lands as a single dragged event would: at its spot if that is free, otherwise in the nearest free slot on its night, shortened if needed. Events you did not select never move. Events that already happened stay; missed ones are planned again at the new place and stay behind as records. Hold Shift to ignore the targets' conditions. One Ctrl+Z undoes the whole move.
- **New layout.** The target list and the selected target's details sit side by side at the top; the calendar runs across the full width below. A bar between them sets how much room each gets. The target details use two columns: progress, exposures and history on the left; conditions, color and notes on the right.
- **Less text on the panel.** The instructions are gone from the panel. They are now tooltips, a Tips section at the top of the settings page (the gear, and Options › Plugins › Target Board), and a "How to use it" list on NINA's plugin page. Status messages are shorter.
- **Padding is gone.** The overhead percentage (15% by default) already covers slewing, centering and focusing, so events no longer keep time free at their ends. Events planned with padding in 1.1.0.0 keep their blocks; the whole block now counts as planned time.

## 1.1.0.0

**Done, partly done, missed.** Each past event now shows what happened, worked out from the frames NINA logged: ✓ done, ◐ partly done, ✗ missed. Finished events are the same block at reduced opacity; missed events have a red dashed outline, and the night's header shows "⚠ missed". The star and tick circles have left the calendar; completion dates are still in each target's history.

**The morning settle.** Nothing changes during the night. At the settle time the next morning (default 08:00, set under *Each morning* in the settings, never before dawn), each finished night is settled once:
- a partly done event shrinks to the time its frames took; its planned block stays as a faint dashed outline;
- the time still missing goes onto that target's next event, which grows up to the end of its slot;
- events in the way move later on their night, or are shortened to fit it, and only go to a later night when less than the shortest event length is left. Shortened time goes back to "to place";
- events placed outside their conditions (with Shift) never move;
- missed events are only marked. Right-click one, or the night's header, to **shift the plan one night later from that night**, or to **add the missed time to each target's next event**. Dragging a missed event onto another night plans its time again and leaves the missed one as a record.

You can also settle a night by hand (right-click its header, once dawn has passed). Every change to the plan can be undone with **Ctrl+Z** or the **Undo** button.

**Selecting several events.** Ctrl+click adds or removes events. Delete removes them, Ctrl+→ and Ctrl+← move them a night, and right-click offers the same.

**Outside the conditions.** Hold **Shift** while dropping, moving or resizing to ignore the target's conditions. Dark time and the horizon (0°) still apply. Such events have a dashed outline and a ⚠ triangle in the bottom-right corner, and the tooltip says how much lies outside.

**Filter view.** Double-click an event to see its frames on the calendar: the frames forecast for an upcoming event, in the order the sequence shoots its rows (row by row, or rotating), and the frames actually taken for a past one. Esc closes it.

**Padding.** Each target has two dropdowns: none, 5, 10 or 15 minutes, before, after or both sides. Padding sits inside the event block, shows hatched, and does not count as imaging time.

**Moon.** A *Moon* switch in the toolbar draws the Moon's altitude as a dotted grey line on the same scale as the target's, with its phase at the highest point.

Also: the sequence readers keep exposure rows in file order (the order NINA shoots them); the Simple Sequencer's *Rotate* mode is read.

## 1.0.4.0

- The target list scrolls by pixels instead of whole rows, so one wheel notch no longer jumps past several targets. Set the speed under **Target list › Mouse wheel speed** in the settings (1× is Windows' usual step).
- Smooth scrolling: the list glides to its new place. Turn it off in the settings if you prefer a jump.
- Click a selected target or set again to deselect it.
- Right-click a target for **Mark as done** and **Hide**; right-click a set's header to do the same for every target in it.
- **Mark as done** is kept on the board only: the sequence file is not changed. The target counts as finished, has nothing left to place, and its history records the date you marked it. **Mark as not done** undoes it and is recorded too. The details panel has the same switch.
- The **Single files** picker in the settings now offers `.xml` and `.ninaTargetSet` files, not just `.json`.

## 1.0.3.0

- Target sets show as one row with their targets nested under it. Click the arrow to fold the set away. Drag the set's row onto a night to plan its targets one after another, in the set's order; any that don't fit stay to place for another night.
- A standalone file that repeats a target from a set (same name, same pointing) folds into that target's row as "Also saved as …". Turn on **Show copies** to list them as their own rows.
- New **Sort by** box: date created (newest or oldest first), last saved, name, progress, or time left to place. The board remembers your choice.
- A target can't be planned for more time than its sequence still needs (plus overhead). Drops, moves and resizes stop at that limit, and a target that is fully planned says so.
- In Night and Week views the altitude curve marks its highest point with the altitude and time.
- The target list is easier to read: larger text, a percent beside each bar, a wider panel, and an exposures table with column headings.

## 1.0.2.0

- Reads Simple Sequencer files too: single targets (`.xml`) and target sets (`.ninaTargetSet`), with planned and done counts per filter.
- The folder watcher also reacts to changes in those files.
- The switches now have their labels beside them (NINA's theme hides checkbox labels).
- The target list follows NINA's dark theme, and each row shows its file name, so copies of a target in different files can be told apart.

## 1.0.1.0

- Fixed: opening the Imaging tab crashed NINA ("This Freezable cannot be frozen"). The calendar now copies NINA's theme colors instead of using its live brushes.
- Errors inside the plugin are now logged and shown as a NINA notification; they no longer take NINA down.
- Saving retries for a moment when another program (antivirus, OneDrive) holds a file.
- The harmless NU1701 build warnings are hidden.

## 1.0.0.0

First version.

- Target list read from NINA sequence and target files (read-only), with per-filter progress.
- Night, Week and Month calendar views; drag targets onto nights; move, resize and split events.
- Events limited to dark time and to each target's conditions; overflow stays on the target to place on another night.
- Conditions: minimum and maximum altitude, above custom horizon, hour angle range, local time window. Altitude and horizon conditions in a sequence are copied over.
- Live log of saved light frames; "done" sessions on the calendar; dated milestones for finished filters and targets.
- Data kept in Documents\N.I.N.A\TargetBoard.
