# Halloween window icon

`master.png` is the orange tabby with a black-and-purple witch hat and gold
buckle, selected on 2026-10-27 to match the other illustrated holiday icons.
It is byte-identical to `../halloween-witch/master.png`; the full generation
prompt and references are recorded in `../halloween-witch/README.md`.
The built-in image-generation tool produced the artwork; generated alpha is
preserved. This replaces the earlier painted-face Halloween artwork.

Run `python cat/seasonal/halloween/export-icons.py` from the repository root with
Pillow available to recreate the PNG frames and PNG-backed ICO. Sizes are 16, 20,
24, 32, 40, 48, 64, 96, 128 and 256 pixels. The script also copies the ICO to the
application's `Assets/Seasonal/halloween.ico` resource. No runtime dependency is
introduced.

## Adding another season

1. Add its transparent master, provenance and icon exports in a sibling directory.
2. Embed its ICO as a WPF `Resource` in `LogReader.App.csproj`.
3. Add a `SeasonalIconDefinition` to `SeasonalIcons.Catalog`, supplying its unique
   ID, pack URI, date rule and priority. No controller change is needed.

All four registered holidays use `HolidayWindowRule`: five local dates before
the holiday, the holiday itself, and one date afterward, inclusive. Halloween
(witch-hat cat) runs October 26–November 1; Thanksgiving (turkey feathers) uses
the fourth Thursday of November (U.S.); Christmas (Santa hat) runs December 20–26;
New Year (party hat) runs December 27–January 2. Thanksgiving in 2026 runs
November 21–27. Weekday and culture settings do not change the seven-date windows.

`HolidayWindowRule(month, day)` uses a fixed annual anchor;
`HolidayWindowRule(month, weekday, occurrence)` uses the nth weekday of a month.
Missing leap days or fifth weekdays do not create a holiday in that year.
`HolidayWeekRule(month, day)` retains Monday-through-Sunday selection for other callers.
`AnnualDateRangeRule(startMonth, startDay, endMonth, endDay)` includes both ends
and supports New Year crossings. Equal start/end selects one day. February 29
holiday weeks exist only in leap years. Ranges compare actual month/day values;
no nonexistent date is substituted. Rules use local Gregorian calendar dates,
independent of culture/week-number settings. Higher priorities win overlaps;
equal priorities preserve catalog order. Christmas hands directly to New Year
without overlap or a standard-icon gap.

The controller refreshes before startup UI, every minute, and on application
activation. Open and subsequently created WPF windows share the selected icon.
Window roots explicitly reference `Icon="{DynamicResource AppWindowIconResource}"`;
new window classes should do the same (implicit base Window styles do not apply
to derived window classes).
For shared launcher deployments, the same refresh also updates the generated
**WeezTail Shared** Start menu shortcut. Seasonal ICO bytes are cached locally per
deployment outside release folders; normal dates restore the shortcut's original
launcher icon reference. Unchanged refreshes do not rewrite the ICO or shortcut.
Expected cache/registration failures are nonfatal and retried on later refreshes.
Executable and launcher resources, desktop shortcuts, installer shortcuts and
existing pinned-item metadata are unchanged. Windows may retain a previous icon
for a pinned taskbar group; pin refresh is not part of this feature.
