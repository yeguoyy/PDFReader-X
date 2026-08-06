import sys
import os
from PIL import Image

def main() -> None:
    src, dst = sys.argv[1], sys.argv[2]
    img = Image.open(src).convert("RGBA")
    print(f"原图尺寸: {img.size}")
    w, h = img.size
    side = min(w, h)
    left = (w - side) // 2
    top = (h - side) // 2
    img = img.crop((left, top, left + side, top + side))
    sizes = [(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)]
    os.makedirs(os.path.dirname(dst), exist_ok=True)
    img.save(dst, format="ICO", sizes=sizes)
    print(f"已生成: {dst} ({os.path.getsize(dst)} bytes)")

if __name__ == "__main__":
    main()
