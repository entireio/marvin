# R2-D2 wheel fit

The simulator uses one broad, smooth roller per foot housing (three total),
sized to LordDiego's artistic mesh rather than replica-builder hardware.

| Housing | Radius | Width | Axle center (x, z) |
| --- | --- | --- | --- |
| Outer, each | 0.055 | 0.120 | ±0.213, -0.1415 |
| Center | 0.050 | 0.090 | 0, 0.1445 |

Dimensions are scene units. Axle height equals radius for ground contact.
The wide rollers use fixed transverse axles to stay within the housings while
turning; rotation follows signed travel. Dirt contacts share these positions
and widths. This is a visual fit, not a physical chassis reconstruction.

## Previous hardware reference (superseded)

Verified September 27, 2026. The previous simulator used the **Media-Conversions
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

The previous two narrow wheels per outer foot did not match the visible
housings, so their hardware dimensions no longer drive simulator geometry.
