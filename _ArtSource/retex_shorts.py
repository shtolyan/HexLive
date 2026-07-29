import numpy as np
from PIL import Image

SRC = '/Volumes/ORICO/HexLive/Assets/ImportedActors/Wear/Shorts_10_14636/Textures/i13SW_Shorts_Diffuse.jpg'
OUT = '/private/tmp/claude-501/-Volumes-ORICO-HexLive/4073f64c-4e72-4b79-b48c-8ddfdaba9401/scratchpad'
PATTERN = f'{OUT}/SetCherry.jpg'

im = Image.open(SRC).convert('RGB').resize((4096, 4096), Image.LANCZOS)
rgb = np.asarray(im, dtype=np.float32) / 255.0
hsv = np.asarray(im.convert('HSV'), dtype=np.float32)
H, S, V = hsv[..., 0] / 255.0, hsv[..., 1] / 255.0, hsv[..., 2] / 255.0

# Fabric mask: denim blue hue, some saturation, not the black void.
hue_d = np.minimum(np.abs(H - 0.61), 1.0 - np.abs(H - 0.61))  # denim hue ~0.61
mask = np.clip(1.0 - hue_d / 0.10, 0, 1) * np.clip((S - 0.08) / 0.15, 0, 1) * np.clip((V - 0.04) / 0.08, 0, 1)
mask = mask[..., None]

lum = 0.299 * rgb[..., 0] + 0.587 * rgb[..., 1] + 0.114 * rgb[..., 2]
fab_mean = lum[mask[..., 0] > 0.5].mean()
shade = np.clip(lum / fab_mean, 0.0, 2.2)[..., None]  # seams/wash/AO detail
print('fabric pixels:', (mask > 0.5).mean(), 'mean lum:', fab_mean)

def save(name, fabric_rgb):
    out = rgb * (1 - mask) + np.clip(fabric_rgb, 0, 1) * mask
    Image.fromarray((out * 255).astype(np.uint8)).save(f'{OUT}/{name}.jpg', quality=92)
    print(name, 'saved')

# 1) Terracotta red
save('ShortsRed', np.array([0.70, 0.22, 0.18], np.float32) * shade)
# 2) Olive khaki
save('ShortsOlive', np.array([0.45, 0.46, 0.26], np.float32) * shade)
# 3) Cherry print on cream, detail multiplied through
pat = np.asarray(Image.open(PATTERN).convert('RGB').resize((1024, 1024)), np.float32) / 255.0
pat = np.tile(pat, (4, 4, 1))
save('ShortsCherry', pat * np.clip(shade, 0, 1.6) * 0.95)
