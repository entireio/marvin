import sys, numpy as np
from PIL import Image
# crop.py NAME x0 y0 x1 y1 scale out  [dirs...]
name, x0, y0, x1, y1, scale, out = sys.argv[1], *map(int, sys.argv[2:6]), float(sys.argv[6]), sys.argv[7]
dirs = sys.argv[8:] or ["sk", "gd"]
ims = [Image.open(f"{d}/{name}.png").convert("RGB").crop((x0, y0, x1, y1)) for d in dirs]
ims = [im.resize((int(im.width*scale), int(im.height*scale)), Image.NEAREST) for im in ims]
w, h = ims[0].size
canvas = Image.new("RGB", (w, h*len(ims)+6*(len(ims)-1)), (255, 0, 255))
for i, im in enumerate(ims): canvas.paste(im, (0, i*(h+6)))
canvas.save(out)
