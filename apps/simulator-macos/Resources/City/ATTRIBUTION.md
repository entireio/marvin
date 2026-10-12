# City art sources

These source assets are CC0 1.0 (public-domain dedication):
https://creativecommons.org/publicdomain/zero/1.0/

- **Human Base Meshes bundle v1.4.1**, Blender Studio and community:
  https://www.blender.org/download/demo-files/ (Human Base Meshes, CC0).
  Original archive: https://download.blender.org/demo/asset-bundles/human-base-meshes/human-base-meshes-bundle-v1.4.1.zip
  `scripts/city/human-source.blend` retains the realistic male/female bodies and
  eyes from that bundle. `crowd.json` is our derivative: clothed, posed, scaled,
  triangulated, decimated, and converted to SceneKit coordinates. No subdivision
  source or Blender runtime is shipped in the application.
- **Clay Plaster**, Amal Kumar / Poly Haven: https://polyhaven.com/a/clay_plaster (2K diffuse, OpenGL normal, roughness; MD5-verified against API manifest).
- **Park Sand**, Poly Haven: https://polyhaven.com/a/park_sand (2K diffuse, OpenGL normal, roughness; downloaded from the API manifest and MD5-verified).
- **Painted Plaster Wall**, Poly Haven: https://polyhaven.com/a/painted_plaster_wall
- **Fabric Pattern 07**, Poly Haven: https://polyhaven.com/a/fabric_pattern_07
- **Kloppenheim 02 Pure Sky**, Poly Haven: https://polyhaven.com/a/kloppenheim_02_puresky (1K Radiance HDR environment).
- **Rusty Metal 03**, Poly Haven: https://polyhaven.com/a/rusty_metal_03

Poly Haven license: https://polyhaven.com/license
The JPGs are the unmodified 2K color, OpenGL normal, and roughness maps.
Material tinting happens in the renderer. No movie stills or franchise
environment art are added by this pass. Architecture, clothing construction, and town layout are
original procedural work. The racing robots retain their existing licenses.

`manifest.json` records the exact SHA-256 hashes of the runtime inputs.
