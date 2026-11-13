using System.Reflection;
using System.Runtime.InteropServices;

// [MANDATORY] Unique id of the plugin. Keep it fixed across releases.
[assembly: Guid("5f0c2a8e-7d41-4b6e-9a3c-2e8d1b7f4c19")]

[assembly: AssemblyVersion("1.4.0.0")]
[assembly: AssemblyFileVersion("1.4.0.0")]

// The plugin name. NINA also uses it to find the options page template ("Target Board_Options").
[assembly: AssemblyTitle("Target Board")]
[assembly: AssemblyDescription("A calendar for your targets: plan sequences across dark nights and track their progress.")]
[assembly: AssemblyCompany("Amogh Bhosekar")]
[assembly: AssemblyProduct("Target Board")]
[assembly: AssemblyCopyright("Copyright © 2026 Amogh Bhosekar")]

[assembly: AssemblyMetadata("MinimumApplicationVersion", "3.2.0.9001")]
[assembly: AssemblyMetadata("License", "MPL-2.0")]
[assembly: AssemblyMetadata("LicenseURL", "https://www.mozilla.org/en-US/MPL/2.0/")]
[assembly: AssemblyMetadata("Repository", "https://github.com/iambad225/NINA_TargetBoard")]
[assembly: AssemblyMetadata("Homepage", "https://github.com/iambad225/NINA_TargetBoard")]
[assembly: AssemblyMetadata("Tags", "Planning,Calendar,Scheduler,Sequencer,Progress")]
[assembly: AssemblyMetadata("ChangelogURL", "https://github.com/iambad225/NINA_TargetBoard/blob/main/CHANGELOG.md")]
[assembly: AssemblyMetadata("FeaturedImageURL", "")]
[assembly: AssemblyMetadata("ScreenshotURL", "")]
[assembly: AssemblyMetadata("AltScreenshotURL", "")]
[assembly: AssemblyMetadata("LongDescription", @"Target Board is a calendar for your imaging targets, in the spirit of Google Calendar.

* Reads your saved sequence files (read-only) and lists every target with its progress, taken from the sequence's own loop counters and NINA's exposure counts.
* Drag a target onto a night to plan it. Events are limited to dark time and to each target's conditions (for example 'altitude above 30°' or 'above my custom horizon'). Whatever does not fit stays on the target for you to place on another night.
* Night, Week and Month views. Move, resize and split events by dragging.
* Logs every light frame NINA saves, so finished work shows on the calendar, and records the date each filter and each target was completed.

Nothing is ever written to your sequences. The board keeps its own files in Documents\N.I.N.A\TargetBoard.

**How to use it**

* Drag a target, or a target set's header, from the list onto a night.
* Hold Shift while you drop, move or resize to ignore the target's conditions. Dark time and the horizon still apply.
* With a target selected, double-click empty time on the calendar to place it there.
* Selecting a target shades the times it can be shot and draws its altitude. Conditions always sit inside dark time, and all ticked ones must hold.
* Drag an event to move it; drag its bottom edge to change its length.
* Ctrl+click selects several events. Drag one of them to move them all together; Delete removes them; Ctrl+→ and Ctrl+← move them a night.
* Double-click an event to see its frames by filter. Esc or a click closes the view.
* Right-click an event, or a night's header, to split it, shift the plan, add missed time or settle the night.
* Ctrl+Z, or the Undo button, takes back the last change to the plan.
* The mouse wheel over the calendar moves it a night at a time: down goes ahead, up goes back. In Month view it moves a month.
* Double-click a row, or right-click it and choose View details, to open its details under the list; Esc or ✕ closes them. Click a selected row again to deselect it. Right-click a row to mark it done or hide it.
* Drag a target set's header to plan its targets one after another, in order.
* ✓ done, ◐ partly done, ✗ missed. Finished events are faded.
* A dashed outline with ⚠ means part of the event lies outside the target's conditions.
* The strip at the right of each night shows when frames were actually taken.")]

[assembly: ComVisible(false)]
[assembly: AssemblyConfiguration("")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]
