#!/usr/bin/env python3
"""Source-level regression checks for the ESP32 relay sketch.

These checks intentionally inspect source structure rather than compiling the
firmware.  The small lexer below preserves character offsets while masking C++
comments and literals, allowing function and block extraction to ignore braces
that are not executable syntax.
"""

from __future__ import annotations

import re
import sys
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
DEFAULT_SKETCH = ROOT / "sketches" / "RelayESP32" / "RelayESP32.ino"
DEFAULT_LIBRARY = ROOT / "libraries" / "ESP32_IR_LTTO" / "ESP32_IR_LTTO.cpp"


class InvariantFailure(AssertionError):
    """Raised when a required source invariant is missing."""


def require(condition: bool, message: str) -> None:
    if not condition:
        raise InvariantFailure(message)


def mask_cpp(source: str, *, mask_literals: bool) -> str:
    """Mask comments and optionally literals without changing source offsets."""

    chars = list(source)
    index = 0
    state = "code"
    quote = ""

    while index < len(source):
        char = source[index]
        next_char = source[index + 1] if index + 1 < len(source) else ""

        if state == "code":
            if char == "/" and next_char == "/":
                chars[index] = chars[index + 1] = " "
                index += 2
                state = "line_comment"
                continue
            if char == "/" and next_char == "*":
                chars[index] = chars[index + 1] = " "
                index += 2
                state = "block_comment"
                continue
            if char in ("'", '"'):
                quote = char
                if mask_literals:
                    chars[index] = " "
                index += 1
                state = "literal"
                continue
            index += 1
            continue

        if state == "line_comment":
            if char in "\r\n":
                state = "code"
            else:
                chars[index] = " "
            index += 1
            continue

        if state == "block_comment":
            if char == "*" and next_char == "/":
                chars[index] = chars[index + 1] = " "
                index += 2
                state = "code"
            else:
                if char not in "\r\n":
                    chars[index] = " "
                index += 1
            continue

        # String or character literal.
        if char == "\\" and next_char:
            if mask_literals:
                if char not in "\r\n":
                    chars[index] = " "
                if next_char not in "\r\n":
                    chars[index + 1] = " "
            index += 2
            continue
        if char == quote:
            if mask_literals:
                chars[index] = " "
            index += 1
            state = "code"
            continue
        if mask_literals and char not in "\r\n":
            chars[index] = " "
        index += 1

    return "".join(chars)


def matching_delimiter(masked: str, start: int, opening: str, closing: str) -> int:
    require(start < len(masked) and masked[start] == opening,
            f"expected {opening!r} at source offset {start}")
    depth = 0
    for index in range(start, len(masked)):
        if masked[index] == opening:
            depth += 1
        elif masked[index] == closing:
            depth -= 1
            if depth == 0:
                return index
    raise InvariantFailure(f"unclosed {opening!r} beginning at source offset {start}")


def skip_space(masked: str, index: int) -> int:
    while index < len(masked) and masked[index].isspace():
        index += 1
    return index


def extract_function(source: str, name: str) -> tuple[str, str]:
    """Return the original and structural-mask text of a named function body."""

    structural = mask_cpp(source, mask_literals=True)
    for match in re.finditer(rf"\b{re.escape(name)}\s*\(", structural):
        open_paren = structural.find("(", match.start())
        close_paren = matching_delimiter(structural, open_paren, "(", ")")
        open_brace = skip_space(structural, close_paren + 1)
        if open_brace >= len(structural) or structural[open_brace] != "{":
            # A call or a forward declaration, not a function definition.
            continue
        close_brace = matching_delimiter(structural, open_brace, "{", "}")
        return (
            source[open_brace + 1:close_brace],
            structural[open_brace + 1:close_brace],
        )
    raise InvariantFailure(f"could not find a definition for {name}()")


def find_crlf_block(function_source: str) -> tuple[str, str]:
    """Find an if-block whose condition recognizes a completed CRLF line."""

    structural = mask_cpp(function_source, mask_literals=True)
    without_comments = mask_cpp(function_source, mask_literals=False)

    for match in re.finditer(r"\bif\s*\(", structural):
        open_paren = structural.find("(", match.start())
        close_paren = matching_delimiter(structural, open_paren, "(", ")")
        condition = without_comments[open_paren + 1:close_paren]
        if not (re.search(r"'\\r'", condition)
                and re.search(r"'\\n'", condition)
                and "&&" in condition):
            continue

        open_brace = skip_space(structural, close_paren + 1)
        require(open_brace < len(structural) and structural[open_brace] == "{",
                "the completed-CRLF branch must use a braced body")
        close_brace = matching_delimiter(structural, open_brace, "{", "}")
        return (
            function_source[open_brace + 1:close_brace],
            structural[open_brace + 1:close_brace],
        )

    raise InvariantFailure("tcpReadTick() has no completed-CRLF branch")


def has_top_level_return_after(masked_body: str, offset: int) -> bool:
    """Check for an unconditional return at the current block depth after offset."""

    depth = 0
    return_offsets = {match.start() for match in re.finditer(r"\breturn\s*;", masked_body)}
    for index, char in enumerate(masked_body):
        if index in return_offsets and index > offset and depth == 0:
            return True
        if char == "{":
            depth += 1
        elif char == "}":
            depth -= 1
    return False


def check_loop_order(source: str) -> None:
    _, loop_masked = extract_function(source, "loop")
    rx_call = re.search(r"\birReceiveTick\s*\(", loop_masked)
    tcp_call = re.search(r"\btcpReadTick\s*\(", loop_masked)
    require(rx_call is not None, "loop() must call irReceiveTick()")
    require(tcp_call is not None, "loop() must call tcpReadTick()")
    require(rx_call.start() < tcp_call.start(),
            "loop() must service irReceiveTick() before tcpReadTick()")


def check_no_software_mute(source: str) -> None:
    executable = mask_cpp(source, mask_literals=True)
    for identifier in ("TX_MUTE_MS", "txMuteUntil"):
        require(re.search(rf"\b{identifier}\b", executable) is None,
                f"obsolete software mute identifier {identifier} must remain absent")


def check_tcp_fairness(source: str) -> None:
    tcp_source, _ = extract_function(source, "tcpReadTick")
    _, crlf_masked = find_crlf_block(tcp_source)
    handle_call = re.search(r"\bhandleLine\s*\(", crlf_masked)
    require(handle_call is not None,
            "completed-CRLF handling must call handleLine()")
    require(has_top_level_return_after(crlf_masked, handle_call.end()),
            "completed-CRLF handling must return after handleLine() so RX can run")


def check_checksum_mapping(source: str) -> None:
    tx_source, _ = extract_function(source, "transmitIrCommand")
    code = mask_cpp(tx_source, mask_literals=False)
    suffix = r"[uUlL]*"
    checksum_mapping = re.compile(
        rf"if\s*\(\s*bits\s*==\s*9{suffix}\s*\)\s*"
        rf"type\s*=\s*"
        rf"\(\s*\(\s*data\s*&\s*0x100{suffix}\s*\)\s*"
        rf"!=\s*0{suffix}\s*\)\s*"
        rf"\?\s*'C'\s*:\s*'P'\s*;",
        re.DOTALL,
    )
    require(checksum_mapping.search(code) is not None,
            "9-bit non-beacons must map data bit 0x100 to 'C' and otherwise to 'P'")


def check_rmt_gap_encoding(source: str) -> None:
    code = mask_cpp(source, mask_literals=False)
    require(re.search(r"#define\s+INTERPACKET_CSUM\s+80000\b", code) is not None,
            "LTTO checksum quiet period must remain 80000us")
    require(re.search(r"RMT_DURATION_MAX_US\s*=\s*0x7FFF[uUlL]*\s*;", code) is not None,
            "legacy RMT duration limit must remain explicit at 15 bits")

    encoder_source, _ = extract_function(source, "encodeLTTO")
    encoder = mask_cpp(encoder_source, mask_literals=False)
    require(re.search(
        r"_gapItemCount\s*=\s*rmtGapItemCount\s*\(\s*"
        r"\(\s*uint32_t\s*\)\s*_endOfPacketDelay\s*\)",
        encoder,
    ) is not None, "encodeLTTO() must reserve the computed number of gap items")
    require(re.search(
        r"_gapPhaseCount\s*=\s*\(\s*uint32_t\s*\)\s*"
        r"rmtGapPhaseCount\s*\(\s*\(\s*uint32_t\s*\)\s*_endOfPacketDelay\s*\)",
        encoder,
    ) is not None, "encodeLTTO() must compute enough 15-bit phases for the gap")
    require(re.search(r"for\s*\([^;]*;\s*_gapItem\s*<\s*_gapItemCount\s*;", encoder) is not None,
            "encodeLTTO() must emit every computed low-gap item")
    require(re.search(r"_basePhaseDuration\s*=\s*[^;]+/\s*_gapPhaseCount\s*;", encoder) is not None,
            "gap duration must be distributed across all computed phases")
    require(re.search(r"duration0\s*=\s*_firstDuration\s*;", encoder) is not None,
            "first split gap duration must be written to duration0")
    require(re.search(r"duration1\s*=\s*_secondDuration\s*;", encoder) is not None,
            "second split gap duration must be written to duration1")
    require(re.search(r"duration[01]\s*=\s*\(\s*_endOfPacketDelay\s*/\s*2\s*\)", encoder) is None,
            "direct half-gap assignment would overflow the 15-bit checksum duration")


def check_final_receive_gap(source: str) -> None:
    decoder_source, _ = extract_function(source, "decodeLTTO")
    decoder = mask_cpp(decoder_source, mask_literals=False)
    require(re.search(
        r"isLastDataItem\s*=\s*\(\s*index\s*==\s*_bitCount\s*\+\s*1\s*\)\s*;",
        decoder,
    ) is not None, "decoder must identify the final data item")
    require(re.search(
        r"if\s*\(\s*!\s*checkData\s*\(\s*rawDataIn\s*,\s*1\s*,\s*1\s*,\s*"
        r"MARK_SPACE\s*\)\s*\)",
        decoder,
    ) is not None, "decoder must validate the 2ms pause after the header")
    require(re.search(
        r"if\s*\(\s*!\s*isLastDataItem\s*&&\s*!\s*checkData\s*\("
        r"\s*rawDataIn\s*,\s*index\s*,\s*1\s*,\s*MARK_SPACE\s*\)\s*\)",
        decoder,
    ) is not None,
            "decoder must not apply the 2ms intermediate-space limit to the extended final gap")

    reader_source, _ = extract_function(source, "readIR")
    reader = mask_cpp(reader_source, mask_literals=False)
    require(re.search(
        r"decoded\s*=\s*decodeLTTO\s*\(\s*item\s*,\s*\(\s*int\s*\)\s*numItems\s*,\s*"
        r"irDataRx\s*\)\s*;",
        reader,
    ) is not None, "readIR() must retain the decoder result for diagnostics")
    require(re.search(r"result\s*=\s*\(\s*int\s*\)\s*valueCount\s*-\s*1\s*;", reader) is not None,
            "bounded RMT captures must be surfaced to the sketch")
    require(re.search(
        r"if\s*\(\s*!\s*decoded\s*\).*?lttoMessage\.type\s*=\s*'V'\s*;.*?"
        r"lttoMessage\.data\s*=\s*0\s*;",
        reader,
        re.DOTALL,
    ) is not None, "invalid bounded captures must surface as rxUnk, never as trusted data")


def run(sketch: Path, library: Path = DEFAULT_LIBRARY) -> None:
    require(sketch.is_file(), f"relay sketch not found: {sketch}")
    require(library.is_file(), f"relay IR library not found: {library}")
    source = sketch.read_text(encoding="utf-8")
    check_loop_order(source)
    check_no_software_mute(source)
    check_tcp_fairness(source)
    check_checksum_mapping(source)
    library_source = library.read_text(encoding="utf-8")
    check_rmt_gap_encoding(library_source)
    check_final_receive_gap(library_source)


def main(argv: list[str]) -> int:
    sketch = Path(argv[1]).resolve() if len(argv) > 1 else DEFAULT_SKETCH
    try:
        run(sketch)
    except (InvariantFailure, OSError, UnicodeError) as error:
        print(f"FAIL: {error}", file=sys.stderr)
        return 1
    print(f"PASS: relay source and RMT timing invariants hold for {sketch}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
