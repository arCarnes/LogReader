# Thanksgiving, Christmas and New Year icons

Created on 2026-10-26 using the built-in image generation tool, with
`../cat-master.png` as the edit target for each image. Generated alpha is
preserved; masters are copied without pixel changes.

- `thanksgiving/master.png`: orange tabby with autumn turkey feathers.
- `christmas/master.png`: orange tabby with a red Santa hat.
- `new-year/master.png`: orange tabby with a gold party hat and confetti.

Each folder includes PNG exports at 16, 20, 24, 32, 40, 48, 64, 96, 128 and
256 pixels, plus a PNG-backed ICO containing all those sizes. Recreate the
exports from the repository root with Pillow available:

```powershell
python cat/seasonal/export-holiday-icons.py
```

All three icons are registered in the application holiday calendar and bundled
as WPF resources. The export script also updates the corresponding application
resources. Each holiday runs from five local dates before its anchor through
one date after, inclusive: Thanksgiving uses the fourth Thursday of November
(U.S.), Christmas December 25, and New Year January 1. Christmas is active
December 20–26, then New Year December 27–January 2. See
`halloween/README.md` for the shared calendar and integration workflow.

## Generation provenance and prompts

All calls used `transparent_background=true` and referenced only the original
`cat/cat-master.png`.

### Thanksgiving

Source output: `exec-f795a5bf-19c1-4bde-8abc-42dab0297f84.png`.

```text
Use case: precise-object-edit. Asset type: WeezTail Thanksgiving application icon, single square transparent PNG. Image 1 is the edit target: the original WeezTail orange tabby cat head. Preserve its exact clean illustrated style, face, orange stripes, green eyes, pink nose, white muzzle/chest, proportions, expression, ears and whiskers. Add one compact fan of broad turkey tail feathers BEHIND the head, visible around the upper and side silhouette, in warm brown, russet, amber and muted gold. Keep this clearly the original cat with Thanksgiving feathers, not a turkey body. Face remains dominant and unobscured. Bold simple feather shapes readable at 16–32 pixels. Center the full head and complete feathers on a square canvas with small even transparent padding. True alpha transparent exterior, internal white fur opaque. No words, numbers, border, drop shadow, background scenery, extra objects or watermark. Clean stray exterior flecks from the original cutout while preserving the intentional whiskers.
```

### Christmas

Source output: `exec-ab435cfa-c7ca-4f72-b10d-a8d79903b866.png`.

```text
Use case: precise-object-edit. Asset type: WeezTail Christmas application icon, single square transparent PNG. Image 1 is the edit target: original WeezTail orange tabby cat head. Preserve its exact clean illustrated style, face, orange stripes, green eyes, pink nose, white muzzle/chest, proportions and expression. Add one red Santa hat with a thick white fur brim and white pompom, tilted between the ears, with its tip softly drooping to one side. Keep both ears recognizable and the face unobscured. Face remains large and dominant. Bold simple shapes readable at 16–32 pixels, matching the existing clean illustrated shading. Center complete head, whiskers, hat and pompom on square canvas with small even transparent padding; no cropping. True alpha transparent exterior, internal white fur stays opaque. No words, year numbers, border, cast shadow, background, snow scenery, other accessories or watermark. Remove accidental stray exterior flecks from original cutout while preserving intentional whiskers.
```

### New Year

Source output: `exec-52475d13-5a64-4795-a46f-386e0c678a62.png`.

```text
Use case: precise-object-edit. Asset type: WeezTail New Year's application icon, single square transparent PNG. Image 1 is the edit target: original WeezTail orange tabby cat head. Preserve its exact clean illustrated style, face, orange stripes, green eyes, pink nose, white muzzle/chest, proportions, expression, ears and whiskers. Add one compact festive GOLD CONICAL PARTY HAT sitting between the ears, with a simple dark gold band and a small gold star at its tip. Add only three or four large clearly separated gold confetti pieces close to the hat, within the same compact silhouette. Keep both ears visible and face unobscured, large and dominant. Use simple bold illustrated shapes with warm metallic gold shading that match the clean illustrated cat, readable at 16–32 pixels. Center complete head, whiskers, full hat, star and confetti on square canvas with small even transparent padding; no cropping. True alpha transparent exterior, internal white fur opaque. No words, year numbers, border, drop shadow, background scenery, fireworks, glasses, extra accessories or watermark. Remove accidental stray exterior flecks from original cutout while preserving intentional whiskers.
```
