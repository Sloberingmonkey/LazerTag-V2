# Debrief hotfix

## Field symptom

The second tagger returned its Tag Summary, then repeatedly returned a detailed
team report containing `0x8A`. The hardened decoder rejected that byte as
non-canonical BCD, so the report was never marked complete and the server kept
requesting it forever.

## Fix

- Preserve exact report-length, team-number, game-ID, and one-byte validation.
- Accept non-canonical one-byte detailed counts using the original host's
  nibble decoder instead of aborting the complete report.
- Log the compatibility case clearly.
- Keep effective-hit normalization, which caps repeated/raw frames at the
  target's available HP.
- Add regression coverage for the observed `0x8A` response from player two.

The ESP32 firmware is unchanged by this hotfix.
