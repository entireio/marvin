import sys, numpy as np
from PIL import Image
# ratio.py NAME out [skdir gddir]: luma ratio map GD/SK, blue=GD darker, red=GD brighter (+-20% full scale), plus SK image dimmed
name, out = sys.argv[1], sys.argv[2]
skd, gdd = (sys.argv[3], sys.argv[4]) if len(sys.argv) > 4 else ("sk", "gd")
def lin(v):
    v = v / 255.0
    return np.where(v <= 0.04045, v / 12.92, ((v + 0.055) / 1.055) ** 2.4)
a = np.asarray(Image.open(f"{skd}/{name}.png").convert("RGB")).astype(float)
b = np.asarray(Image.open(f"{gdd}/{name}.png").convert("RGB")).astype(float)
la = (lin(a) * [0.2126, 0.7152, 0.0722]).sum(-1); lb = (lin(b) * [0.2126, 0.7152, 0.0722]).sum(-1)
r = np.log2((lb + 1e-3) / (la + 1e-3))
t = np.clip(r / 0.263, -1, 1)  # +-20%
img = np.zeros(a.shape)
img[..., 0] = np.clip(t, 0, 1) * 255
img[..., 2] = np.clip(-t, 0, 1) * 255
img[..., 1] = (1 - np.abs(t)) * a[..., 1] * 0.35
Image.fromarray(img.astype(np.uint8)).save(out)
