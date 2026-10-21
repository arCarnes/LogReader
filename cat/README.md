# Application icon

The icon is the orange tabby head from the supplied WeezTech image, with the
lettering and exterior background removed. The white fur and whiskers remain
opaque.

- `cat-master.png`: transparent 1254 × 1254 master.
- `cat_<size>.png`: transparent square exports at 16, 20, 24, 32, 40, 48, 64,
  96, 128, 256, 512, and 1024 pixels.
- `cat.ico`: includes all export sizes through 256 pixels.
- `../LogReader/LogReader.App/Assets/favicon.ico`: identical application copy,
  shared by the WPF windows, executable, launchers, and installer.

The master was edited with the built-in image generation tool. Prompt:

> Use case: background-extraction. Edit target: attached orange tabby cat
> WeezTech logo. Remove all WeezTech lettering completely and remove the exterior
> white background to true transparency. Preserve the cat head's exact design,
> orange fur, green eyes, pink nose, white muzzle, white chest, fine white
> whiskers, facial markings and proportions. White areas INSIDE the cat remain
> opaque. Only the cat head, no text, no shadow, no border, no background. Center
> the complete head including ears, chin and whiskers on a square transparent
> canvas with a small even margin, suitable for an application icon. Faithful
> clean cutout, no redesign.

PNG exports use Pillow's Lanczos resampling from the master. The ICO contains
PNG frames generated from the same master.

Seasonal window icons use separate assets and leave this standard icon intact.
See [the Halloween asset and extension guide](seasonal/halloween/README.md) for
the first seasonal icon and instructions for adding another holiday.
