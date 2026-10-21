# Halloween window icon

`master.png` is the approved generated transparent-face preview from the seasonal
icon conversation on 2026-10-21. It is preserved without redesign. The reference
was a painting of an orange cat in a niche; its artist and title remain unidentified.
No attribution or license for that original painting has been established.

The built-in image-generation tool created the preview using this instruction:

> Cut out only the cat's head including both complete ears, cheeks, muzzle and
> chin. Remove the wall, dark niche, body and chest to real alpha transparency.
> Preserve the strange humanlike face, narrow eyes, expression, proportions,
> orange/cream colors and painted texture. Center the head on a square canvas
> with a small transparent margin. No border, shadow, text or added objects.

Source preview: `exec-bc583d6f-54e2-4a36-b8a0-eea2bd1539bc.png`.

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

`HolidayWeekRule(month, day)` selects Monday through Sunday containing that date.
`AnnualDateRangeRule(startMonth, startDay, endMonth, endDay)` includes both ends
and supports New Year crossings. Equal start/end selects one day. February 29
holiday weeks exist only in leap years. Ranges compare actual month/day values;
no nonexistent date is substituted. Rules use local Gregorian calendar dates,
independent of culture/week-number settings. Higher priorities win overlaps;
equal priorities preserve catalog order. Only Halloween is registered today.

The controller refreshes before startup UI, every minute, and on application
activation. Open and subsequently created WPF windows share the selected icon.
It never changes executable, launcher, shortcut or pinned-item metadata. Windows
may retain the standard shortcut icon for a pinned taskbar group.
