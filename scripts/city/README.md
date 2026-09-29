# Crowd export

Normal app builds use bundled `Resources/City/crowd.json` and never need Blender,
Python packages, or network access. Run `git lfs pull` after cloning.

To edit clothing or poses, use Blender 4.5 LTS (tested with 4.5.9):

```sh
blender -b scripts/city/human-source.blend \
  --python scripts/city/export-crowd.py -- \
  apps/simulator-macos/Resources/City
python3 scripts/city/check-assets.py --update
apps/simulator-macos/build-app.sh
```

The CC0 source subset preserves authored realistic heads, faces, hands, and
anatomy. The exporter builds garments, poses meshes, and generates three indexed
LODs (~6,500 / 1,600 / 350 triangles) for twelve body/pose/outfit combinations.
The output has semantic surface IDs for skin, cloth, and leather; runtime colors
vary without creating a material per resident. No source body is rendered bare.

Verify new output using `--town-smoke-test OUTPUT_DIRECTORY`, including the
`town-citizens.png` close-up, and `--town-benchmark OUTPUT_DIRECTORY` at 1080p.
Retain the three LODs and inspect standing and seated poses before updating the
asset manifest. See `Resources/City/ATTRIBUTION.md` for provenance and licensing.
