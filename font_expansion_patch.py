#!/usr/bin/env python3
"""3DS智龙迷城字库扩容工具（补丁版 code_Update.bin）

功能：
- 读取集字表，扩展00_rod_db.bin字符映射
- 生成La4格式纹理
- 输出主字体(db)和粗体(eb)两套TIMG纹理+PNG预览
- Patch code_Update.bin中的字体度量参数（输出为 code.bin）

源文件目录应包含：
- code_Update.bin       （补丁可执行文件）
- DreamHanSans-W17.ttc  （字体文件）
- cnv_rod_pro_eb.timg   （参考TIMG）
- 集字表.txt             （UTF-8文本）

补丁偏移已通过 IDA MCP 工具验证（IDA挂载 code_Update.bin）：
- 4个ARM patch + 5个Float patch 偏移整体 +0x1BC38（相对本体 code.bin）
- 第5个ARM patch：寄存器从 r5 变为 r7（指令 0xE3A05033 → 0xE3A07033）
- patch参数(目标值)与原 font_expansion.py 完全一致
"""

import argparse
import struct
import math
import os
import sys
from PIL import Image, ImageFont, ImageDraw

TILE_ORDER = [0, 1, 4, 5, 2, 3, 6, 7, 8, 9, 12, 13, 10, 11, 14, 15]

GLYPH_WIDTH = 16
GLYPH_HEIGHT = 17
FONT_SIZE = 15
TEXTURE_SIZE = 1024
COLS_PER_ROW = 64
CROP_WIDTH = 1024
CROP_HEIGHT = 912
HEADER_FIELDS_SIZE = 0xA0

FLAG_DB = 0x00010103
FLAG_EB = 0x00100103

# 补丁 code_Update.bin 的 Float patch 偏移（IDA验证：0x25C364/0x25C368/0x25C36C/0x25C384/0x25C388）
# IDA地址 = 文件偏移 + 0x100000（.text段基址）
# 与原 font_expansion.py 的目标值完全一致
FLOAT_PATCHES = {
    0x15C364: 16.0,
    0x15C368: 13.0,
    0x15C36C: 15.0,
    0x15C384: 17.0,
    0x15C388: 17.0,
}

# 补丁 code_Update.bin 的 ARM patch 偏移（IDA验证）
# 前4个偏移整体 +0x1BC38，指令对与原 font_expansion.py 一致
# 第5个patch：偏移 0x2B40B8，原指令 0xE3A07033（mov r7, #0x33），新指令 0xE3A07040（mov r7, #0x40）
# 注意：补丁版编译器将值 0x33 分配给 r7（而非本体的 r5），但 patch 目标值 0x40 与原脚本一致
ARM_PATCHES = {
    0x15C21C: (0xE3A08003, 0xE3A08001),  # mov r8, #3 -> #1
    0x15C220: (0xE3A0400E, 0xE3A0400D),  # mov r4, #14 -> #13
    0x15C224: (0xE3A09002, 0xE3A09001),  # mov r9, #2 -> #1
    0x15C228: (0xE3A0A013, 0xE3A0A00F),  # mov r10, #19 -> #15
    0x2B40B8: (0xE3A07033, 0xE3A07040),  # mov r7, #0x33 -> #0x40 (寄存器从r5变为r7)
}

BOLD_OFFSETS = [(-1, 0), (1, 0), (0, -1), (0, 1)]


def tile_position(x, y):
    return TILE_ORDER[x % 4 + y % 4 * 4] + 16 * (x // 4) + 32 * (y // 4)


def encode_tile_texture(la4_data, width, height):
    import numpy as np
    src = np.frombuffer(la4_data, dtype=np.uint8).reshape(height, width)
    tiles_x = width // 8
    tiles_y = height // 8
    dst_idx = np.zeros(64, dtype=np.int32)
    for i in range(64):
        x = i % 8
        y = i // 8
        dst_idx[i] = TILE_ORDER[x % 4 + y % 4 * 4] + 16 * (x // 4) + 32 * (y // 4)
    blocks = np.lib.stride_tricks.sliding_window_view(src, (8, 8))
    blocks = blocks[::8, ::8].reshape(-1, 64)
    output = np.zeros((len(blocks), 64), dtype=np.uint8)
    output[:, dst_idx] = blocks
    return output.tobytes()


def read_char_table(path):
    chars = []
    seen = set()
    with open(path, 'r', encoding='utf-8') as f:
        for line in f:
            line = line.strip()
            if not line or line.startswith('#'):
                continue
            for ch in line:
                if ch not in seen:
                    chars.append(ch)
                    seen.add(ch)
    return chars


def read_rod_db(path):
    with open(path, 'rb') as f:
        data = f.read()
    dwords = []
    for i in range(0, len(data) // 4 * 4, 4):
        dwords.append(struct.unpack_from('<I', data, i)[0])
    return dwords


def dword_to_char(dword):
    be_bytes = dword.to_bytes(4, 'big')
    utf8_bytes = be_bytes.lstrip(b'\x00')
    if not utf8_bytes:
        return None
    try:
        return utf8_bytes.decode('utf-8')
    except UnicodeDecodeError:
        return None


def char_to_dword(ch):
    utf8_bytes = ch.encode('utf-8')
    if len(utf8_bytes) > 4:
        raise ValueError(f"Character U+{ord(ch):04X} UTF-8 too long ({len(utf8_bytes)} bytes)")
    return int.from_bytes(utf8_bytes, 'big')


def render_glyph(ch, font, bold=False):
    img = Image.new('LA', (GLYPH_WIDTH, GLYPH_HEIGHT), (255, 0))
    draw = ImageDraw.Draw(img)
    # 使用 anchor='la'（left-ascender）让所有字符的 ascender 顶部对齐到统一 y 坐标
    # 解决 bbox 居中导致的标点强制居中、横线高度不齐问题
    ascent, descent = font.getmetrics()
    y = (GLYPH_HEIGHT - (ascent + descent)) // 2
    advance = font.getlength(ch)
    # 整数除法确保字符完整显示在字格内（避免 0.5px 偏移导致右侧被切）
    # advance=15, GLYPH_WIDTH=16, x=0 让 ink 范围 [0,15] 完整落在字格内
    x = (GLYPH_WIDTH - int(advance)) // 2
    if bold:
        for dx, dy in BOLD_OFFSETS:
            draw.text((x + dx, y + dy), ch, font=font, fill=(0, 255), anchor='la')
    draw.text((x, y), ch, font=font, fill=(0, 255), anchor='la')
    return img


def image_to_la4(img):
    import numpy as np
    la_img = img.convert('LA')
    arr = np.array(la_img, dtype=np.uint8)
    l4 = (arr[:, :, 0] >> 4).astype(np.uint8)
    a4 = (arr[:, :, 1] >> 4).astype(np.uint8)
    la4 = ((l4 << 4) | a4).flatten()
    return la4.tobytes()


def build_font_texture(chars, font, bold=False):
    cols = COLS_PER_ROW
    rows = math.ceil(len(chars) / cols)
    needed_height = rows * GLYPH_HEIGHT

    if needed_height > TEXTURE_SIZE:
        raise ValueError(
            f"Texture overflow: {len(chars)} glyphs need {needed_height}px, max {TEXTURE_SIZE}px"
        )

    tex_img = Image.new('LA', (TEXTURE_SIZE, TEXTURE_SIZE), (255, 0))

    for i, ch in enumerate(chars):
        if ch is None:
            continue
        col = i % cols
        row = i // cols
        glyph = render_glyph(ch, font, bold=bold)
        tex_img.paste(glyph, (col * GLYPH_WIDTH, row * GLYPH_HEIGHT))

    la4 = image_to_la4(tex_img)
    tiled = encode_tile_texture(la4, TEXTURE_SIZE, TEXTURE_SIZE)
    return tiled, cols, rows, tex_img


def parse_timg_chunks(data):
    chunks = {}
    chunk_order = []
    offset = HEADER_FIELDS_SIZE

    while offset + 12 <= len(data):
        sig = data[offset:offset + 8]
        size = struct.unpack_from('<I', data, offset + 8)[0]
        if size < 12 or offset + size > len(data):
            break

        sig_str = sig.rstrip(b'\x00').decode('ascii', errors='replace')
        chunks[sig_str] = data[offset:offset + size]
        chunk_order.append(sig_str)

        offset += size
        if 'end' in sig_str:
            break

    return chunks, chunk_order


def build_timg(ref_data, flags, filename, crop_w, crop_h, txd_pixel_data):
    header = bytearray(ref_data[:HEADER_FIELDS_SIZE])

    struct.pack_into('<I', header, 0x08, flags)

    name_bytes = filename.encode('ascii')[:63]
    header[0x10:0x50] = b'\x00' * 64
    header[0x10:0x10 + len(name_bytes)] = name_bytes

    struct.pack_into('<H', header, 0x94, crop_w)
    struct.pack_into('<H', header, 0x96, crop_h)

    ref_chunks, chunk_order = parse_timg_chunks(ref_data)

    output = bytearray(header)

    for name in chunk_order:
        if 'txd' in name:
            orig = ref_chunks[name]
            sig = orig[:8]
            new_size = 12 + len(txd_pixel_data)
            output.extend(sig)
            output.extend(struct.pack('<I', new_size))
            output.extend(txd_pixel_data)
        else:
            output.extend(ref_chunks[name])

    return bytes(output)


def patch_code_bin(data):
    data = bytearray(data)

    for offset, value in FLOAT_PATCHES.items():
        if offset + 4 <= len(data):
            struct.pack_into('<f', data, offset, value)
        else:
            print(f"  Warning: code.bin offset 0x{offset:X} out of range")

    for offset, (old_instr, new_instr) in ARM_PATCHES.items():
        if offset + 4 <= len(data):
            current = struct.unpack_from('<I', data, offset)[0]
            if current != old_instr:
                print(f"  Warning: code.bin[0x{offset:X}] expected 0x{old_instr:08X}, found 0x{current:08X}")
            struct.pack_into('<I', data, offset, new_instr)
        else:
            print(f"  Warning: code.bin offset 0x{offset:X} out of range")

    return bytes(data)


def save_preview(img, path):
    rgba = Image.new('RGBA', img.size, (30, 30, 30, 255))
    src = img.convert('RGBA')
    rgba.paste(src, mask=src.split()[3])
    rgba.save(path)


def main():
    parser = argparse.ArgumentParser(description='3DS智龙迷城字库扩容工具（补丁版 code_Update.bin）')
    parser.add_argument('--char-table', required=True, help='集字表文件(UTF-8文本)')
    parser.add_argument('--code-bin', required=True, help='原始 code_Update.bin（补丁可执行文件）')
    parser.add_argument('--rod-db-bin', help='原始00_rod_db.bin（--fresh模式下不需要）')
    parser.add_argument('--font', required=True, help='字体文件(DreamHanSans-W17.ttc)')
    parser.add_argument('--output-dir', required=True, help='输出目录')
    parser.add_argument('--reference-timg', required=True, help='参考TIMG文件(cnv_rod_pro_eb.timg)')
    parser.add_argument('--font-index', type=int, default=0, help='TTC字体索引(默认0)')
    parser.add_argument('--fresh', action='store_true', help='纯集字表模式：不继承原字库字符，仅用集字表构建')
    args = parser.parse_args()

    if not args.fresh and args.rod_db_bin is None:
        parser.error('--rod-db-bin is required unless --fresh is used')

    os.makedirs(args.output_dir, exist_ok=True)

    print("[1/7] Reading character table...")
    new_chars = read_char_table(args.char_table)
    print(f"  New characters: {len(new_chars)}")

    if args.fresh:
        print("[2/7] Fresh mode: skipping 00_rod_db.bin...")
        existing_dwords = []
        print(f"  Existing entries: 0 (fresh mode)")
    else:
        print("[2/7] Reading 00_rod_db.bin...")
        existing_dwords = read_rod_db(args.rod_db_bin)
        print(f"  Existing entries: {len(existing_dwords)}")

    print("[3/7] Building new 00_rod_db.bin...")
    dword_set = set(existing_dwords)
    added = 0
    new_dwords = list(existing_dwords)
    for ch in new_chars:
        dw = char_to_dword(ch)
        if dw not in dword_set:
            new_dwords.append(dw)
            dword_set.add(dw)
            added += 1
    print(f"  Added: {added}, Total: {len(new_dwords)}")

    all_chars = []
    for dw in new_dwords:
        ch = dword_to_char(dw)
        all_chars.append(ch)

    print("[4/7] Loading font...")
    font = ImageFont.truetype(args.font, FONT_SIZE, index=args.font_index)

    print("[5/7] Parsing reference TIMG...")
    with open(args.reference_timg, 'rb') as f:
        ref_data = f.read()

    if ref_data[:4] != b'TIMG':
        print("Error: Reference file is not a valid TIMG", file=sys.stderr)
        sys.exit(1)

    ref_chunks, chunk_order = parse_timg_chunks(ref_data)
    print(f"  Chunks: {chunk_order}")

    cols = COLS_PER_ROW
    rows = math.ceil(len(all_chars) / cols)
    crop_w = CROP_WIDTH
    crop_h = rows * GLYPH_HEIGHT
    print(f"  Layout: {cols} cols x {rows} rows, crop: {crop_w}x{crop_h}")

    print("[6/7] Generating font textures...")

    print("  Rendering main font (db)...")
    db_tiled, _, _, db_img = build_font_texture(all_chars, font, bold=False)
    db_timg = build_timg(ref_data, FLAG_DB, "cnv_rod_pro_db", crop_w, crop_h, db_tiled)
    db_path = os.path.join(args.output_dir, "cnv_rod_pro_db.timg")
    with open(db_path, 'wb') as f:
        f.write(db_timg)
    print(f"  -> {db_path}")
    save_preview(db_img, os.path.join(args.output_dir, "cnv_rod_pro_db.png"))

    print("  Rendering bold font (eb)...")
    eb_tiled, _, _, eb_img = build_font_texture(all_chars, font, bold=True)
    eb_timg = build_timg(ref_data, FLAG_EB, "cnv_rod_pro_eb", crop_w, crop_h, eb_tiled)
    eb_path = os.path.join(args.output_dir, "cnv_rod_pro_eb.timg")
    with open(eb_path, 'wb') as f:
        f.write(eb_timg)
    print(f"  -> {eb_path}")
    save_preview(eb_img, os.path.join(args.output_dir, "cnv_rod_pro_eb.png"))

    print("[7/7] Writing bin files...")

    rod_db_path = os.path.join(args.output_dir, "00_rod_db.bin")
    with open(rod_db_path, 'wb') as f:
        for dw in new_dwords:
            f.write(struct.pack('<I', dw))
    print(f"  -> {rod_db_path}")

    with open(args.code_bin, 'rb') as f:
        code_data = f.read()
    patched = patch_code_bin(code_data)
    # 补丁版输入文件为 code_Update.bin，输出统一重命名为 code.bin
    code_path = os.path.join(args.output_dir, "code.bin")
    with open(code_path, 'wb') as f:
        f.write(patched)
    print(f"  -> {code_path} (patched from {os.path.basename(args.code_bin)})")

    print("\nDone! All files written to:", args.output_dir)


if __name__ == '__main__':
    main()
