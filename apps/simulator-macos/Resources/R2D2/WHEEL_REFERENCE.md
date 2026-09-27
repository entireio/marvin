# R2-D2 wheel reference

Verified September 27, 2026. This simulator adopts the **Media-Conversions
Colson replica-builder configuration**. It is not a claim about the wheel
hardware of a particular Lucasfilm prop; builder designs vary.

| Location | Referenced hardware | Diameter | Tread width |
| --- | --- | --- | --- |
| Outer feet | Four Colson 2.00005.441 wheels, two per foot | 5 in / 127 mm | 1.25 in / 31.75 mm |
| Center foot | Colson 1.03054.441 swivel caster | 3 in / 76.2 mm | 0.875 in / 22.225 mm |

Primary references:

- [Media-Conversions outer drive parts](https://media-conversions.net/r2d2/images/foot.drive/R2.outer.foot.drive.parts.html): specifies four 5-by-1.25-inch wheels, part 2.00005.441.
- [Media-Conversions center-foot parts](https://media-conversions.net/r2d2/images/SAK.center.foot/R2.SAK.center.foot.parts.html): specifies the 3-inch 1 Series Colson caster option. The same page lists alternative omni-wheel designs, which are not modeled here.
- [Center-foot assembly](https://r2d2.media-conversions.net/images/SAK.center.foot/R2.SAK.center.foot.assm.html): describes the single stem-caster mounting stack.
- [Colson Performa specifications](https://colsoncaster.com/performa/): 1 Series tread width 7/8 inch; 2 Series tread width 1-1/4 inches; flat/round rubber tread options.
- [Colson 1 Series catalog, threaded-stem table, printed page 14](https://resources.colsongroup.com/colson/pdfs/series/ColsonCatalog2020_R3_1Series.pdf): 1.03054.441 dimensions.

## Simulator mapping and limits

Wheel dimensions are converted from inches through 0.0254 meters/inch, using
0.885 scene units / 1.08 meters for R2-D2's established height scale. The center
wheel and straight-line impression are therefore **70% of the outer tire width**.
Geometry and dirt effects read the same tire definitions in `R2D2.swift`.
Smooth rubber replaces the invented raised tread bars and grooved footprints.
Different radii determine the wheel rotation rate.

The outer axle locations and center mounting position fit LordDiego's artistic
foot mesh; these positions, hub details and simplified caster steering are not
engineering dimensions from the cited build. Ground decals approximate the
full tread width; tire deformation, contact pressure, caster trail dynamics and
soil displacement are not physically simulated. This correction establishes
sourced wheel sizes, not a dimensionally exact replica of the complete chassis.
