# R2-D2

**R2-D2**, by **LordDiego**, published January 29, 2021.
https://sketchfab.com/3d-models/r2-d2-9e6b5bc13f7943d08e657bffce78fc90

License: **Creative Commons Attribution 4.0 International (CC BY 4.0)**.
https://creativecommons.org/licenses/by/4.0/

Downloaded September 27, 2026 from public attachments to the PlayCanvas support
thread https://forum.playcanvas.com/t/how-to-use-zip-files-in-playcanvas/21616 :

- `source-original.zip`: original FBX and texture archive, posted by Dehaml01.
  https://github.com/playcanvas/playcanvas-gltf/files/7067347/r2-d2.zip
  SHA-256: b2535b16ff7e142a515ca9be2c380680ee645521f54628a9761439fb34449216
- `source-gltf.zip`: Blender/glTF conversion posted by kungfooman.
  https://github.com/playcanvas/playcanvas-gltf/files/7068044/r2d2_gltf.zip
  SHA-256: ae567fae88d38c22b43b897cae16dd0999887c1016a17a074cae1cb29e559f74

The original archive's eleven texture names match the creator's Sketchfab
texture metadata, including R2D2_Emission, R2D2_Barrel_Normal, and TantiveIV.
The conversion contains the matching 25.2k-triangle robot and presentation floor.
Both source archives are preserved; the exporter verifies their hashes.

Simulator modifications: bake the source animation's four-second deployed
three-leg pose, keep the head facing forward, exclude the presentation floor,
preserve the 25,158 robot triangles and smooth normals/UVs, scale the robot to
0.85 scene units, and lift its foot housings 0.035 units for added rolling tires.
Four outer drive wheels and one center caster rotate from signed simulation travel;
these wheels are simulator additions, not original artist geometry. Dimensions
follow the documented Colson builder setup in [WHEEL_REFERENCE.md](WHEEL_REFERENCE.md).
The dome
turns gently about its original inclined pivot. Original base-color, emission,
metalness, roughness and barrel normal textures are used.

This replaces the earlier low-poly Eric Finn model. R2-D2 and Star Wars are
properties of their respective owners; this fan-made model does not imply
any affiliation or endorsement. The CC license does not grant trademark rights.
