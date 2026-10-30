#!/usr/bin/env python3
"""Erzeugt die Marken-Assets für LiftUp ohne externe Abhängigkeiten.

Ergebnis:
  src/LiftUp.App/Assets/LiftUp.ico     Anwendungssymbol (16-256 px)
  installer/assets/banner.bmp          493 x 58  (WixUI-Banner)
  installer/assets/dialog.bmp          493 x 312 (WixUI-Hintergrund)

Aufruf:  python3 installer/make-assets.py
"""

from __future__ import annotations

import os
import struct
import zlib

NAVY = (31, 42, 68)
ACCENT = [(76, 141, 255), (106, 165, 255), (143, 190, 255)]
WHITE = (255, 255, 255)

GLYPHS = {
    "L": ["10000", "10000", "10000", "10000", "10000", "10000", "11111"],
    "I": ["11111", "00100", "00100", "00100", "00100", "00100", "11111"],
    "F": ["11111", "10000", "10000", "11110", "10000", "10000", "10000"],
    "T": ["11111", "00100", "00100", "00100", "00100", "00100", "00100"],
    "U": ["10001", "10001", "10001", "10001", "10001", "10001", "01110"],
    "P": ["11110", "10001", "10001", "11110", "10000", "10000", "10000"],
}


class Canvas:
    def __init__(self, width: int, height: int, background=NAVY):
        self.width = width
        self.height = height
        self.pixels = [[(*background, 255) for _ in range(width)] for _ in range(height)]

    def set(self, x: int, y: int, color, alpha: int = 255):
        if 0 <= x < self.width and 0 <= y < self.height:
            self.pixels[y][x] = (*color, alpha)

    def rect(self, x: int, y: int, w: int, h: int, color, alpha: int = 255):
        for py in range(max(0, y), min(self.height, y + h)):
            for px in range(max(0, x), min(self.width, x + w)):
                self.pixels[py][px] = (*color, alpha)

    def rounded(self, x: int, y: int, w: int, h: int, radius: int, color):
        for py in range(y, y + h):
            for px in range(x, x + w):
                cx = min(max(px, x + radius), x + w - radius - 1)
                cy = min(max(py, y + radius), y + h - radius - 1)
                dx, dy = px - cx, py - cy
                if dx * dx + dy * dy <= radius * radius:
                    self.set(px, py, color)

    def wordmark(self, x: int, y: int, scale: int, color=WHITE, spacing: int | None = None):
        spacing = spacing if spacing is not None else scale
        cursor = x
        for char in "LIFTUP":
            glyph = GLYPHS[char]
            for row, line in enumerate(glyph):
                for column, bit in enumerate(line):
                    if bit == "1":
                        self.rect(cursor + column * scale, y + row * scale, scale, scale, color)
            cursor += 5 * scale + spacing


def supersampled(size: int, factor: int = 4):
    big = Canvas(size * factor, size * factor, NAVY)
    radius = int(size * factor * 0.2)
    big.rounded(0, 0, size * factor, size * factor, radius, NAVY)

    bar_width = int(size * factor * 0.15)
    gap = int(size * factor * 0.07)
    left = int(size * factor * 0.19)
    bottom = int(size * factor * 0.78)
    heights = [0.20, 0.33, 0.46]
    for index, share in enumerate(heights):
        height = int(size * factor * share)
        big.rounded(left + index * (bar_width + gap), bottom - height, bar_width, height,
                    int(bar_width * 0.25), ACCENT[index])

    # Box-Filter auf Zielgröße.
    pixels = []
    for y in range(size):
        row = bytearray()
        for x in range(size):
            r = g = b = a = 0
            for sy in range(factor):
                for sx in range(factor):
                    pr, pg, pb, pa = big.pixels[y * factor + sy][x * factor + sx]
                    r += pr * pa
                    g += pg * pa
                    b += pb * pa
                    a += pa
            count = factor * factor
            if a == 0:
                row += bytes((0, 0, 0, 0))
            else:
                row += bytes((r // a, g // a, b // a, a // count))
        pixels.append(bytes(row))
    return pixels


def png(width: int, height: int, rows: list[bytes]) -> bytes:
    def chunk(tag: bytes, data: bytes) -> bytes:
        body = tag + data
        return struct.pack(">I", len(data)) + body + struct.pack(">I", zlib.crc32(body) & 0xFFFFFFFF)

    raw = b"".join(b"\x00" + row for row in rows)
    return (b"\x89PNG\r\n\x1a\n"
            + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(raw, 9))
            + chunk(b"IEND", b""))


def dib(rows: list[bytes]) -> bytes:
    height = len(rows)
    width = len(rows[0]) // 4
    header = struct.pack("<IiiHHIIiiII", 40, width, height * 2, 1, 32, 0, 0, 0, 0, 0, 0)
    body = bytearray()
    for row in reversed(rows):          # BMP ist von unten nach oben
        for index in range(0, len(row), 4):
            r, g, b, a = row[index:index + 4]
            body += bytes((b, g, r, a))
    mask_row = ((width + 31) // 32) * 4
    body += b"\x00" * (mask_row * height)
    return header + bytes(body)


def write_ico(path: str, sizes=(16, 24, 32, 48, 64, 128, 256)):
    images = []
    for size in sizes:
        rows = supersampled(size)
        payload = png(size, size, rows) if size >= 256 else dib(rows)
        images.append((size, payload))

    header = struct.pack("<HHH", 0, 1, len(images))
    offset = 6 + 16 * len(images)
    directory = b""
    for size, payload in images:
        dimension = 0 if size >= 256 else size
        directory += struct.pack("<BBBBHHII", dimension, dimension, 0, 0, 1, 32, len(payload), offset)
        offset += len(payload)

    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "wb") as handle:
        handle.write(header + directory)
        for _, payload in images:
            handle.write(payload)
    return path


def write_bmp(path: str, canvas: Canvas):
    width, height = canvas.width, canvas.height
    stride = (width * 3 + 3) & ~3
    header = struct.pack("<IiiHHIIiiII", 40, width, height, 1, 24, 0, stride * height, 0, 0, 0, 0)
    file_header = b"BM" + struct.pack("<IHHI", 14 + 40 + stride * height, 0, 0, 54)
    body = bytearray()
    for y in reversed(range(height)):
        row = bytearray()
        for x in range(width):
            r, g, b, _ = canvas.pixels[y][x]
            row += bytes((b, g, r))
        row += b"\x00" * (stride - len(row))
        body += row
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "wb") as handle:
        handle.write(file_header + header + bytes(body))
    return path


def banner(path: str):
    canvas = Canvas(493, 58, NAVY)
    canvas.rect(0, 56, 493, 2, ACCENT[0])
    canvas.wordmark(22, 15, 4)
    canvas.rect(374, 22, 10, 10, ACCENT[0])
    canvas.rect(388, 22, 10, 10, ACCENT[1])
    canvas.rect(402, 22, 10, 10, ACCENT[2])
    return write_bmp(path, canvas)


def dialog(path: str):
    canvas = Canvas(493, 312, NAVY)
    for index in range(3):
        canvas.rect(300 + index * 60, 0, 45, 312, ACCENT[index], 40)
    canvas.wordmark(46, 118, 7)
    canvas.rect(46, 196, 180, 3, ACCENT[0])
    canvas.rect(46, 210, 120, 3, ACCENT[1])
    return write_bmp(path, canvas)


if __name__ == "__main__":
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    print(write_ico(os.path.join(root, "src", "LiftUp.App", "Assets", "LiftUp.ico")))
    print(banner(os.path.join(root, "installer", "assets", "banner.bmp")))
    print(dialog(os.path.join(root, "installer", "assets", "dialog.bmp")))
