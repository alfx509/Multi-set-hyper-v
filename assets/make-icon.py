"""Draws the MultiSeat icon (four screens = four players) and saves src/MultiSeat/MultiSeat.ico + preview PNG."""
from PIL import Image, ImageDraw

S = 1024
img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
d = ImageDraw.Draw(img)
d.rounded_rectangle((32, 32, S - 32, S - 32), radius=200, fill=(37, 99, 235, 255))

colors = [(255, 255, 255), (134, 239, 172), (253, 224, 71), (251, 146, 160)]
pad, gap = 170, 70
cell = (S - 2 * pad - gap) // 2
for i, c in enumerate(colors):
    x = pad + (i % 2) * (cell + gap)
    y = pad - 20 + (i // 2) * (cell + gap)
    h = int(cell * 0.70)
    d.rounded_rectangle((x, y, x + cell, y + h), radius=36, fill=c + (255,))
    d.rectangle((x + cell // 2 - 22, y + h, x + cell // 2 + 22, y + h + 46), fill=(219, 234, 254, 255))
    d.rounded_rectangle((x + cell // 2 - 90, y + h + 40, x + cell // 2 + 90, y + h + 70), radius=14, fill=(219, 234, 254, 255))

img.save("src/MultiSeat/MultiSeat.ico", sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
img.resize((256, 256), Image.LANCZOS).save("assets/MultiSeat.png")
