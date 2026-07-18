#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""MSBT text import/export tool for 智龙迷城 (パズドラ) game localization.

Usage:
    python msbt_tool.py export <input.msbt> <output.csv>
    python msbt_tool.py import <original.msbt> <translated.csv> <output.msbt>
    python msbt_tool.py batch-export <input_dir> <output_dir>
    python msbt_tool.py batch-import <original_dir> <csv_dir> <output_dir>
    python msbt_tool.py info <input.msbt>

The MSBT variant used by this game encodes TXT2 strings as UTF-8 (not UTF-16LE)
with single-byte 0x00 terminators and 0x0E-prefixed control codes.
"""

import struct
import csv
import argparse
import sys
import os
import re

# ---------------------------------------------------------------------------
# Constants
# ---------------------------------------------------------------------------
MSBT_MAGIC = b'MsgStdBn'
HEADER_SIZE = 0x20           # 32 bytes
SECTION_HEADER_SIZE = 0x10  # 16 bytes
ALIGNMENT = 16
CTRL_MARKER = 0x0E
# FileSize field is a u32 LE at offset 0x12 in the 32-byte header
# (matches C# reference implementation & actual game files; note the spec text
#  that places it at 0x14 is off by two bytes).
FILE_SIZE_OFFSET = 0x12
NSECTION_OFFSET = 0x0E

# Match <CTRL:...> where content is anything up to the next '>'. Used both for
# counting markers and for hex validation. Kept for backward compatibility.
CTRL_PATTERN = re.compile(r'<CTRL:([^>]*)>')

# Match any known escape marker: <color:RRGGBBAA>, <size:N>, <var:str>,
# <var:num>, <var:idx:N>, <emoji>, <special>, <ruby:...>, <CTRL:HEX>.
# Used for counting markers in cmd_import validation and text_to_bytes.
ESCAPE_PATTERN = re.compile(
    r'<(?:'
    r'color:[0-9A-Fa-f]{8}'
    r'|size:\d+'
    r'|var:str'
    r'|var:num'
    r'|var:idx:\d+'
    r'|emoji'
    r'|special'
    r'|ruby:[^>]*'
    r'|CTRL:[^>]*'
    r')>'
)


# ---------------------------------------------------------------------------
# Exceptions
# ---------------------------------------------------------------------------
class InvalidControlCode(Exception):
    """Raised when a <CTRL:...> marker contains invalid hex content."""

    def __init__(self, hex_str):
        self.hex_str = hex_str
        super().__init__('invalid control code hex: %r' % hex_str)


class MsbtFormatError(Exception):
    """Raised on structural MSBT errors (bad magic, truncated, ...)."""


# ---------------------------------------------------------------------------
# Section representation
# ---------------------------------------------------------------------------
class MsbtSection:
    """A single MSBT section: 16-byte header + data + alignment padding.

    Attributes:
        magic       : 4-byte section magic (e.g. b'LBL1', b'TXT2').
        size        : u32 LE data size (from header).
        reserved    : 8-byte reserved field from header.
        data        : raw section data bytes (length == size).
        padding     : raw alignment padding bytes after data.
        file_offset : absolute file offset where the 16-byte header starts.
    """

    def __init__(self, magic, size, reserved, data, padding, file_offset):
        self.magic = magic
        self.size = size
        self.reserved = reserved
        self.data = data
        self.padding = padding
        self.file_offset = file_offset


# ---------------------------------------------------------------------------
# Control code helpers
# ---------------------------------------------------------------------------
def parse_control_code(buf, pos):
    """Parse a control code starting at ``buf[pos]`` (must be 0x0E).

    Layout: 0x0E(1) + extra(4) + size1(2 LE) + payload(size1 bytes).
    Returns (cc_bytes, new_pos).
    """
    n = len(buf)
    if pos + 7 > n:
        raise MsbtFormatError('truncated control code header at %d' % pos)
    size1 = struct.unpack_from('<H', buf, pos + 5)[0]
    total = 7 + size1
    if pos + total > n:
        raise MsbtFormatError('truncated control code payload at %d' % pos)
    return buf[pos:pos + total], pos + total


def _control_code_to_text(cc):
    """Convert a control code's raw bytes to a readable escape string.

    Returns the escape text (e.g. '<color:FFFF00FF>') or None if the type
    is unknown (caller should fall back to <CTRL:HEX>).

    Control code layout: 0x0E(1) + extra(4) + size1(2) + payload(size1).
    - extra high16 = category: 0=ruby/var, 1=variable, 2=size, 3=color
    """
    extra = struct.unpack_from('<I', cc, 1)[0]
    size1 = struct.unpack_from('<H', cc, 5)[0]
    payload = cc[7:7 + size1]

    if extra == 0x00030000 and size1 == 4:
        # 颜色码: payload = RGBA(4)
        return '<color:%s>' % payload.hex().upper()
    if extra == 0x00020000 and size1 == 2:
        # 字号码: payload = N(2 LE)
        n = struct.unpack_from('<H', payload, 0)[0]
        return '<size:%d>' % n
    if extra == 0x00000001 and size1 == 0:
        # 字符串变量（玩家名）
        return '<var:str>'
    if extra == 0x00010001 and size1 == 0:
        # 数值变量
        return '<var:num>'
    if extra == 0x00030001 and size1 == 1:
        # 变量索引: payload = index(1)
        return '<var:idx:%d>' % payload[0]
    if extra == 0x00000002 and size1 == 0:
        # 表情图标
        return '<emoji>'
    if extra == 0x00020001 and size1 == 0:
        # 特殊字符模式
        return '<special>'
    if extra == 0x00000000 and size1 > 0:
        # Ruby 注音: payload = type(2 LE) + furi_len(2 LE) + furi_text(UTF-8)
        # 始终输出 <ruby:TYPE:假名>（含 type=0），消除假名以"数字:"开头时的歧义
        if size1 < 4:
            return None  # 格式异常，兜底
        type_val = struct.unpack_from('<H', payload, 0)[0]
        furi_len = struct.unpack_from('<H', payload, 2)[0]
        furi_text = payload[4:4 + furi_len].decode('utf-8', errors='replace')
        return '<ruby:%d:%s>' % (type_val, furi_text)
    return None  # 未知类型，兜底


def _escape_marker_to_bytes(marker):
    """Convert a single escape marker string to raw control code bytes.

    Raises InvalidControlCode for invalid <CTRL:HEX> content.
    """
    if marker.startswith('<color:'):
        rgba = bytes.fromhex(marker[7:-1])
        return b'\x0E' + struct.pack('<I', 0x00030000) + struct.pack('<H', 4) + rgba
    if marker.startswith('<size:'):
        n = int(marker[6:-1])
        return b'\x0E' + struct.pack('<I', 0x00020000) + struct.pack('<H', 2) + struct.pack('<H', n)
    if marker == '<var:str>':
        return b'\x0E' + struct.pack('<I', 0x00000001) + struct.pack('<H', 0)
    if marker == '<var:num>':
        return b'\x0E' + struct.pack('<I', 0x00010001) + struct.pack('<H', 0)
    if marker.startswith('<var:idx:'):
        n = int(marker[9:-1])
        return b'\x0E' + struct.pack('<I', 0x00030001) + struct.pack('<H', 1) + bytes([n])
    if marker == '<emoji>':
        return b'\x0E' + struct.pack('<I', 0x00000002) + struct.pack('<H', 0)
    if marker == '<special>':
        return b'\x0E' + struct.pack('<I', 0x00020001) + struct.pack('<H', 0)
    if marker.startswith('<ruby:'):
        content = marker[6:-1]  # strip '<ruby:' and '>'
        # 正向始终输出 <ruby:TYPE:假名>（含 type=0）以消除歧义。
        # 向后兼容：无 type 前缀的旧格式 <ruby:假名> 视为 type=0。
        m = re.match(r'(\d+):(.*)', content, re.DOTALL)
        if m:
            type_val = int(m.group(1))
            furi_text = m.group(2)
        else:
            type_val = 0
            furi_text = content
        furi_bytes = furi_text.encode('utf-8')
        furi_len = len(furi_bytes)
        payload = struct.pack('<H', type_val) + struct.pack('<H', furi_len) + furi_bytes
        return b'\x0E' + struct.pack('<I', 0x00000000) + struct.pack('<H', len(payload)) + payload
    if marker.startswith('<CTRL:'):
        hex_str = marker[6:-1]
        if len(hex_str) % 2 != 0 or not re.fullmatch(r'[0-9A-Fa-f]+', hex_str):
            raise InvalidControlCode(hex_str)
        return bytes.fromhex(hex_str)
    raise InvalidControlCode(marker)


def read_string_bytes(section_data, rel_off):
    """Read a TXT2 string starting at ``section_data[rel_off]``.

    Scans bytes, treating 0x0E as control-code start (control codes may contain
    embedded 0x00 bytes) and 0x00 outside a control code as the terminator.
    Returns the raw content bytes (terminator excluded).
    """
    pos = rel_off
    n = len(section_data)
    out = bytearray()
    while pos < n:
        b = section_data[pos]
        if b == 0x00:
            return bytes(out)
        if b == CTRL_MARKER:
            cc, pos = parse_control_code(section_data, pos)
            out.extend(cc)
        else:
            out.append(b)
            pos += 1
    raise MsbtFormatError('unterminated string at relative offset %d' % rel_off)


def bytes_to_text(raw, filter_ruby=False):
    """Convert raw string bytes (no terminator) to text with readable escape markers.

    Known control code types are escaped to readable names for translator
    readability:
    - <color:RRGGBBAA>  for color codes (extra=0x00030000)
    - <size:N>          for font size codes (extra=0x00020000)
    - <var:str>         for string variable (extra=0x00000001)
    - <var:num>         for numeric variable (extra=0x00010001)
    - <var:idx:N>       for variable index (extra=0x00030001)
    - <emoji>           for emoji icon (extra=0x00000002)
    - <special>         for special character mode (extra=0x00020001)
    - <ruby:假名>        for ruby/furigana (extra=0x00000000, size1>0)

    Unknown control code types fall back to <CTRL:HEX>.

    When ``filter_ruby`` is True, ruby control codes (extra=0x00000000,
    size1>0) are skipped entirely: their bytes are consumed and discarded,
    and no marker is emitted. All other control codes are escaped to
    readable names.
    """
    out = []
    pos = 0
    n = len(raw)
    while pos < n:
        b = raw[pos]
        if b == CTRL_MARKER:
            cc, pos = parse_control_code(raw, pos)
            extra = struct.unpack_from('<I', cc, 1)[0]
            size1 = struct.unpack_from('<H', cc, 5)[0]
            if filter_ruby and extra == 0x00000000 and size1 > 0:
                # Skip ruby control code entirely.
                continue
            text = _control_code_to_text(cc)
            if text is not None:
                out.append(text)
            else:
                # Unknown type: fall back to raw hex.
                out.append('<CTRL:%s>' % cc.hex().upper())
        else:
            j = pos
            while j < n and raw[j] != CTRL_MARKER:
                j += 1
            out.append(bytes(raw[pos:j]).decode('utf-8'))
            pos = j
    return ''.join(out)


def text_to_bytes(text):
    """Convert text with readable escape markers back to raw bytes (no terminator).

    Recognizes all readable escape formats (<color:...>, <size:...>,
    <var:str>, <var:num>, <var:idx:...>, <emoji>, <special>, <ruby:...>)
    and the backward-compatible <CTRL:HEX> fallback.

    Raises InvalidControlCode if a <CTRL:HEX> marker contains non-hex or
    odd-length content.
    """
    out = bytearray()
    pos = 0
    n = len(text)
    while pos < n:
        m = ESCAPE_PATTERN.search(text, pos)
        if m is None:
            out.extend(text[pos:].encode('utf-8'))
            break
        if m.start() > pos:
            out.extend(text[pos:m.start()].encode('utf-8'))
        out.extend(_escape_marker_to_bytes(m.group(0)))
        pos = m.end()
    return bytes(out)


# ---------------------------------------------------------------------------
# TXT2 section
# ---------------------------------------------------------------------------
class Txt2Section:
    """Parses and holds the content of a TXT2 section.

    ``strings`` is a list of ``(abs_file_offset, raw_bytes)`` tuples ordered by
    string index.
    """

    def __init__(self, section):
        self.section = section
        self.strings = []
        self.num_strings = 0
        self._parse()

    def _parse(self):
        data = self.section.data
        if len(data) < 4:
            raise MsbtFormatError('TXT2 section too small')
        self.num_strings = struct.unpack_from('<I', data, 0)[0]
        if self.num_strings == 0:
            return
        needed = 4 + 4 * self.num_strings
        if len(data) < needed:
            raise MsbtFormatError('TXT2 offset table truncated')
        offsets = struct.unpack_from('<%dI' % self.num_strings, data, 4)
        for off in offsets:
            abs_off = self.section.file_offset + SECTION_HEADER_SIZE + off
            raw = read_string_bytes(data, off)
            self.strings.append((abs_off, raw))


# ---------------------------------------------------------------------------
# MSBT file
# ---------------------------------------------------------------------------
class MsbtFile:
    """Parses and holds an entire MSBT file: 32-byte header + ordered sections."""

    def __init__(self, data):
        if len(data) < HEADER_SIZE:
            raise MsbtFormatError('file smaller than header')
        if data[:8] != MSBT_MAGIC:
            raise MsbtFormatError('bad magic')
        self.raw = data
        self.header = bytearray(data[:HEADER_SIZE])
        self.n_sections = struct.unpack_from('<H', data, NSECTION_OFFSET)[0]
        self.sections = []
        offset = HEADER_SIZE
        for _ in range(self.n_sections):
            if offset + SECTION_HEADER_SIZE > len(data):
                raise MsbtFormatError('truncated section header')
            magic = bytes(data[offset:offset + 4])
            size = struct.unpack_from('<I', data, offset + 4)[0]
            reserved = bytes(data[offset + 8:offset + 16])
            data_start = offset + SECTION_HEADER_SIZE
            data_end = data_start + size
            if data_end > len(data):
                raise MsbtFormatError('truncated section data for %r' % magic)
            sec_data = bytes(data[data_start:data_end])
            next_offset = (data_end + ALIGNMENT - 1) & ~(ALIGNMENT - 1)
            padding = bytes(data[data_end:next_offset])
            self.sections.append(
                MsbtSection(magic, size, reserved, sec_data, padding, offset))
            offset = next_offset

    def find_section(self, magic):
        for s in self.sections:
            if s.magic == magic:
                return s
        return None


# ---------------------------------------------------------------------------
# Export command
# ---------------------------------------------------------------------------
def cmd_export(input_path, output_path, filter_ruby=False, copy_translated=False):
    try:
        with open(input_path, 'rb') as f:
            data = f.read()
    except OSError as e:
        sys.stderr.write('Error: cannot read input file: %s\n' % e)
        return 1

    if len(data) < 8 or data[:8] != MSBT_MAGIC:
        sys.stderr.write('Error: not a valid MSBT file (bad magic)\n')
        return 1
    try:
        msbt = MsbtFile(data)
    except MsbtFormatError as e:
        sys.stderr.write('Error: %s\n' % e)
        return 1

    txt2_sec = msbt.find_section(b'TXT2')
    if txt2_sec is None:
        sys.stderr.write('Error: TXT2 section not found\n')
        return 1
    try:
        txt2 = Txt2Section(txt2_sec)
    except MsbtFormatError as e:
        sys.stderr.write('Error: %s\n' % e)
        return 1

    try:
        with open(output_path, 'w', encoding='utf-8-sig', newline='') as f:
            writer = csv.writer(f)
            writer.writerow(['pointer_address', 'original', 'translated'])
            for abs_off, raw in txt2.strings:
                text = bytes_to_text(raw, filter_ruby)
                translated = text if copy_translated else ''
                writer.writerow(['0x%X' % abs_off, text, translated])
    except OSError as e:
        sys.stderr.write('Error: cannot write output file: %s\n' % e)
        return 1

    sys.stderr.write('Exported %d strings from %s\n'
                     % (txt2.num_strings, os.path.basename(input_path)))
    return 0


# ---------------------------------------------------------------------------
# Import command
# ---------------------------------------------------------------------------
def cmd_import(original_path, csv_path, output_path, filter_ruby=False):
    try:
        with open(original_path, 'rb') as f:
            data = f.read()
    except OSError as e:
        sys.stderr.write('Error: cannot read original file: %s\n' % e)
        return 1

    if len(data) < 8 or data[:8] != MSBT_MAGIC:
        sys.stderr.write('Error: not a valid MSBT file (bad magic)\n')
        return 1
    try:
        msbt = MsbtFile(data)
    except MsbtFormatError as e:
        sys.stderr.write('Error: %s\n' % e)
        return 1

    txt2_sec = msbt.find_section(b'TXT2')
    if txt2_sec is None:
        sys.stderr.write('Error: TXT2 section not found\n')
        return 1
    try:
        txt2 = Txt2Section(txt2_sec)
    except MsbtFormatError as e:
        sys.stderr.write('Error: %s\n' % e)
        return 1
    num_strings = txt2.num_strings

    # --- read CSV ---
    rows = []
    try:
        with open(csv_path, 'r', encoding='utf-8-sig', newline='') as f:
            reader = csv.reader(f)
            header = next(reader, None)
            if header is None:
                sys.stderr.write('Error: CSV file is empty\n')
                return 1
            for idx, row in enumerate(reader):
                line_no = idx + 2  # header is line 1
                if len(row) < 3:
                    sys.stderr.write(
                        'Error: CSV line %d has insufficient columns\n'
                        % line_no)
                    return 1
                rows.append((line_no, row))
    except OSError as e:
        sys.stderr.write('Error: cannot read CSV file: %s\n' % e)
        return 1

    if len(rows) != num_strings:
        sys.stderr.write('Error: CSV has %d rows, but MSBT has %d strings\n'
                         % (len(rows), num_strings))
        return 1

    # --- build new string bytes ---
    new_string_bytes = []
    for i, (line_no, row) in enumerate(rows):
        _abs_off, orig_raw = txt2.strings[i]
        orig_text = bytes_to_text(orig_raw, filter_ruby)
        orig_cc_count = len(ESCAPE_PATTERN.findall(orig_text))

        original_col = row[1]
        translated_col = row[2]

        if translated_col == '':
            sys.stderr.write(
                'Warning: line %d has empty translation, using original\n'
                % line_no)
            text = original_col
        else:
            text = translated_col

        # Validate escape marker count (must match original after filter_ruby).
        new_cc_count = len(ESCAPE_PATTERN.findall(text))
        if new_cc_count != orig_cc_count:
            sys.stderr.write(
                'Error: line %d has %d control codes, expected %d\n'
                % (line_no, new_cc_count, orig_cc_count))
            return 1

        # Convert text to bytes (also validates hex content of CTRL markers).
        try:
            raw = text_to_bytes(text)
        except InvalidControlCode as e:
            sys.stderr.write(
                'Error: line %d has invalid control code <CTRL:%s>\n'
                % (line_no, e.hex_str))
            return 1

        new_string_bytes.append(raw + b'\x00')

    # --- rebuild TXT2 section data ---
    new_offsets = []
    running = 4 + 4 * num_strings
    for sb in new_string_bytes:
        new_offsets.append(running)
        running += len(sb)

    txt2_data = bytearray()
    txt2_data.extend(struct.pack('<I', num_strings))
    for off in new_offsets:
        txt2_data.extend(struct.pack('<I', off))
    for sb in new_string_bytes:
        txt2_data.extend(sb)
    txt2_data = bytes(txt2_data)

    txt2_new_size = len(txt2_data)
    txt2_old_size = txt2_sec.size

    # --- rebuild whole file ---
    out = bytearray()
    out.extend(msbt.header)  # 32 bytes; fileSize updated afterwards

    for sec in msbt.sections:
        if sec.magic == b'TXT2':
            # Rebuilt section header: original magic + new size + original reserved.
            new_sec_header = bytearray(SECTION_HEADER_SIZE)
            new_sec_header[0:4] = sec.magic
            struct.pack_into('<I', new_sec_header, 4, txt2_new_size)
            new_sec_header[8:16] = sec.reserved
            out.extend(new_sec_header)
            out.extend(txt2_data)
            # Alignment padding.
            data_end = len(out)
            next_align = (data_end + ALIGNMENT - 1) & ~(ALIGNMENT - 1)
            pad_len = next_align - data_end
            if txt2_new_size == txt2_old_size:
                # Round-trip: preserve original padding bytes (0xAB in game files).
                if pad_len <= len(sec.padding):
                    out.extend(sec.padding[:pad_len])
                else:
                    out.extend(sec.padding)
                    out.extend(b'\x00' * (pad_len - len(sec.padding)))
            else:
                out.extend(b'\x00' * pad_len)
        else:
            # Non-TXT2 section: preserve header + data + padding verbatim.
            out.extend(sec.magic)
            out.extend(struct.pack('<I', sec.size))
            out.extend(sec.reserved)
            out.extend(sec.data)
            out.extend(sec.padding)

    # Update fileSize field (u32 LE at 0x12).
    new_file_size = len(out)
    struct.pack_into('<I', out, FILE_SIZE_OFFSET, new_file_size)

    try:
        with open(output_path, 'wb') as f:
            f.write(bytes(out))
    except OSError as e:
        sys.stderr.write('Error: cannot write output file: %s\n' % e)
        return 1

    sys.stderr.write('Imported %d strings, new file size: %d bytes\n'
                     % (num_strings, new_file_size))
    return 0


# ---------------------------------------------------------------------------
# Info command
# ---------------------------------------------------------------------------
def cmd_info(input_path):
    """Print structural info about an MSBT file to stdout."""
    try:
        with open(input_path, 'rb') as f:
            data = f.read()
    except OSError as e:
        sys.stderr.write('Error: cannot read input file: %s\n' % e)
        return 1

    if len(data) < 8 or data[:8] != MSBT_MAGIC:
        sys.stderr.write('Error: not a valid MSBT file (bad magic)\n')
        return 1
    try:
        msbt = MsbtFile(data)
    except MsbtFormatError as e:
        sys.stderr.write('Error: %s\n' % e)
        return 1

    n_sec = struct.unpack_from('<H', data, NSECTION_OFFSET)[0]
    fs = struct.unpack_from('<I', data, FILE_SIZE_OFFSET)[0]
    print('File: %s' % os.path.basename(input_path))
    print('  File size:      %d bytes (header says %d, %s)'
          % (len(data), fs, 'OK' if fs == len(data) else 'MISMATCH'))
    print('  Sections:       %d' % n_sec)
    for sec in msbt.sections:
        size = sec.size
        pad = len(sec.padding)
        cc_count = 0
        str_count = 0
        if sec.magic == b'TXT2':
            try:
                t = Txt2Section(sec)
                str_count = t.num_strings
                for _, raw in t.strings:
                    cc_count += sum(1 for b in raw if b == CTRL_MARKER)
            except MsbtFormatError:
                str_count = -1
        print('  %-4s  offset=0x%X  size=%d  padding=%d  strings=%d  ctrl_codes=%d'
              % (sec.magic.decode('ascii', 'replace'),
                 sec.file_offset, size, pad, str_count, cc_count))
    return 0


# ---------------------------------------------------------------------------
# Batch commands
# ---------------------------------------------------------------------------
def cmd_batch_export(input_dir, output_dir, filter_ruby=False, copy_translated=False):
    """Export every .msbt in input_dir to a .csv in output_dir."""
    if not os.path.isdir(input_dir):
        sys.stderr.write('Error: input directory not found: %s\n' % input_dir)
        return 1
    try:
        os.makedirs(output_dir, exist_ok=True)
    except OSError as e:
        sys.stderr.write('Error: cannot create output dir: %s\n' % e)
        return 1

    files = sorted(f for f in os.listdir(input_dir) if f.lower().endswith('.msbt'))
    if not files:
        sys.stderr.write('Warning: no .msbt files found in %s\n' % input_dir)

    ok = 0
    fail = 0
    total_strings = 0
    for fn in files:
        src = os.path.join(input_dir, fn)
        csv_path = os.path.join(output_dir, fn[:-5] + '.csv')
        rc = cmd_export(src, csv_path, filter_ruby, copy_translated)
        if rc == 0:
            ok += 1
            # Count strings for the summary.
            try:
                with open(src, 'rb') as f:
                    data = f.read()
                m = MsbtFile(data)
                txt2 = m.find_section(b'TXT2')
                if txt2:
                    total_strings += Txt2Section(txt2).num_strings
            except Exception:
                pass
        else:
            fail += 1
    sys.stderr.write('Batch export: %d OK, %d failed, %d total strings\n'
                     % (ok, fail, total_strings))
    return 0 if fail == 0 else 1


def cmd_batch_import(original_dir, csv_dir, output_dir, filter_ruby=False):
    """Import every .csv in csv_dir (matching .msbt in original_dir) to output_dir."""
    if not os.path.isdir(original_dir):
        sys.stderr.write('Error: original directory not found: %s\n' % original_dir)
        return 1
    if not os.path.isdir(csv_dir):
        sys.stderr.write('Error: csv directory not found: %s\n' % csv_dir)
        return 1
    try:
        os.makedirs(output_dir, exist_ok=True)
    except OSError as e:
        sys.stderr.write('Error: cannot create output dir: %s\n' % e)
        return 1

    csvs = sorted(f for f in os.listdir(csv_dir) if f.lower().endswith('.csv'))
    if not csvs:
        sys.stderr.write('Warning: no .csv files found in %s\n' % csv_dir)

    ok = 0
    fail = 0
    skipped = 0
    for csv_fn in csvs:
        # Derive the .msbt filename: foo.csv -> foo.msbt
        msbt_fn = csv_fn[:-4] + '.msbt'
        msbt_path = os.path.join(original_dir, msbt_fn)
        csv_path = os.path.join(csv_dir, csv_fn)
        out_path = os.path.join(output_dir, msbt_fn)
        if not os.path.isfile(msbt_path):
            sys.stderr.write('Warning: no matching .msbt for %s, skipping\n' % csv_fn)
            skipped += 1
            continue
        rc = cmd_import(msbt_path, csv_path, out_path, filter_ruby)
        if rc == 0:
            ok += 1
        else:
            fail += 1
    sys.stderr.write('Batch import: %d OK, %d failed, %d skipped\n'
                     % (ok, fail, skipped))
    return 0 if fail == 0 else 1


# ---------------------------------------------------------------------------
# CLI
# ---------------------------------------------------------------------------
def build_parser():
    parser = argparse.ArgumentParser(
        description='MSBT text import/export tool for 智龙迷城 (パズドラ).')
    sub = parser.add_subparsers(dest='command')

    p_export = sub.add_parser('export', help='Export MSBT strings to a CSV file.')
    p_export.add_argument('input', help='Input .msbt file path')
    p_export.add_argument('output', help='Output .csv file path')
    p_export.add_argument('-r', '--filter-ruby', action='store_true',
                          help='Filter out ruby (furigana) control codes from export')
    p_export.add_argument('-c', '--copy-to-translated', action='store_true',
                          help='Copy original text into the translated column '
                               '(including control code markers)')

    p_import = sub.add_parser('import',
                              help='Rebuild an MSBT file from an original '
                                   'MSBT and a translated CSV.')
    p_import.add_argument('original', help='Original .msbt file path')
    p_import.add_argument('csv', help='Translated .csv file path')
    p_import.add_argument('output', help='Output .msbt file path')
    p_import.add_argument('-r', '--filter-ruby', action='store_true',
                          help='CSV was exported with --filter-ruby; skip ruby '
                               'control code count validation accordingly')

    p_bexport = sub.add_parser('batch-export',
                               help='Export every .msbt in a directory to CSVs.')
    p_bexport.add_argument('input_dir', help='Directory containing .msbt files')
    p_bexport.add_argument('output_dir', help='Directory to write .csv files')
    p_bexport.add_argument('-r', '--filter-ruby', action='store_true',
                           help='Filter out ruby (furigana) control codes from export')
    p_bexport.add_argument('-c', '--copy-to-translated', action='store_true',
                           help='Copy original text into the translated column '
                                '(including control code markers)')

    p_bimport = sub.add_parser('batch-import',
                               help='Rebuild .msbt files from a directory of CSVs.')
    p_bimport.add_argument('original_dir', help='Directory with original .msbt files')
    p_bimport.add_argument('csv_dir', help='Directory with translated .csv files')
    p_bimport.add_argument('output_dir', help='Directory to write rebuilt .msbt files')
    p_bimport.add_argument('-r', '--filter-ruby', action='store_true',
                           help='CSVs were exported with --filter-ruby')

    p_info = sub.add_parser('info', help='Print structural info about an MSBT file.')
    p_info.add_argument('input', help='Input .msbt file path')

    return parser


def main(argv=None):
    parser = build_parser()
    args = parser.parse_args(argv)

    if args.command == 'export':
        return cmd_export(args.input, args.output, args.filter_ruby, args.copy_to_translated)
    if args.command == 'import':
        return cmd_import(args.original, args.csv, args.output, args.filter_ruby)
    if args.command == 'batch-export':
        return cmd_batch_export(args.input_dir, args.output_dir, args.filter_ruby, args.copy_to_translated)
    if args.command == 'batch-import':
        return cmd_batch_import(args.original_dir, args.csv_dir, args.output_dir, args.filter_ruby)
    if args.command == 'info':
        return cmd_info(args.input)

    parser.print_help(sys.stderr)
    return 1


if __name__ == '__main__':
    sys.exit(main())
