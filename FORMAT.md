# The CVC1 bitstream (ManyCam 2.5.x)

Notes for anyone implementing CVC1 elsewhere. Reconstructed from ManyCam's encoder (`MCVEnc.dll`, ManyCam 2.5.68);
the reference implementation is [`src/Decoder.cs`](src/Decoder.cs) and the constant tables are in [`src/Tables.cs`](src/Tables.cs).

CVC1 is derived from **AVS1-P2 (Jizhun)**: the transform, dequantisation tables, intra prediction, interpolation
filters and loop filter are AVS's; the headers, macroblock syntax and VLC tables are ManyCam's own.
Points marked **(quirk)** differ from AVS and must be reproduced exactly or the picture drifts.

## Container

AVI, video only, FourCC `CVC1` in `strf` (the `strh` handler is blank), written by DirectShow's AVI Mux
(OpenDML `ix00`/`indx` plus a legacy `idx1`; padding entries named `7Fxx`). The frame rate in the header is
nominal (always 15 fps) and frames are stored in **decode order**. ManyCam writes the header at the end of
recording, so interrupted recordings have a zeroed header; the `00db` chunks are still intact.

## Bit packing

All fields are MSB-first. `ue(v)` and `se(v)` are order-0 Exp-Golomb codes (H.264 mapping for `se`).

## Frame

```
[sequence header]     I frames only
slice ...             one or more
0xFFFFFF0A            32 bits, end of frame
```

**Sequence header** (byte aligned): `0xFFFFFF08` (32), width (16), height (16), four 8-bit fields
(observed `1F 1F 1F 1F`, not needed for decoding).

**Slice header:**

| bits | field |
|---|---|
| 24 | `0xFFFFFF` |
| 2 | `01` |
| 6 | first row of the slice, in 8-pixel block rows |
| 16 | `len` = data bytes + 4. If `0xFFFF`, a 24-bit value follows holding data bytes + 7 |
| 2 | picture type: 0 = I, 1 = P, 2 = B, 3 = P that references the *last I or type-3* picture |
| 6 | QP |
| 1 | flag: per-macroblock QP adaptation (B pictures only) |
| 7 | zero |

The slice data follows and is padded with a `1` bit plus zeros to a byte boundary.
Slice height = difference between consecutive slices' first rows (a single slice is common).
Neighbours above a slice start are unavailable for prediction, and the loop filter skips the slice's top edge.

**GOP structure seen:** I every 90 frames, type-3 P every 15, otherwise alternating P and B in decode order
(`I P B P B ... 3 B P B ...`). Display order: a B picture is shown *before* the anchor that precedes it in the file.
References: type-1 P uses the most recent anchor (I/P/3); type-3 P uses the most recent I or type-3 picture;
B uses the two most recent anchors (past and future).

## Macroblocks

16×16 luma + two 8×8 chroma blocks (4:2:0), raster order.

### I pictures
Every MB is intra; there is no MB type field.

### P pictures
```
ue(skip_run)                      skipped MBs before the next coded MB
mb_type   '0' 16x16 | '10' intra | '110' 16x8 | '1110' 8x16
```
The encoder caps `skip_run` (it forces a coded MB once 251 are pending) and always codes the last MB of a slice,
so slices never end in a skip run.

### B pictures
```
ue(skip_run)
mb_type   '0' direct | '10' 16x16 | '110' intra
[qp_ind]  only if the slice flag is set and type != intra: '0' = QP, '10' = QP+2, '11' = QP-2
[dir]     16x16 only: 0 = future anchor, 1 = past anchor
```
With the slice flag set, intra MBs use a position-dependent QP: +2 within two MBs of the picture border,
−2 in the central area (|mbx − W/32| ≤ 3 or |mby − H/32| ≤ 2), otherwise the slice QP.

### Intra MB
```
for each 8x8 luma block (raster):
    1 bit: 1 = use most-probable mode, else 2 bits rem_mode (mode = rem < mpm ? rem : rem+1)
    1 bit: coded
    if coded: intra luma coefficients
ue(chroma_mode)
U: 1 bit coded [+ chroma coefficients]
V: 1 bit coded [+ chroma coefficients]
```
`mpm = min(top, left)`, or 2 (DC) if either is unavailable or inter. Luma modes: 0 vertical, 1 horizontal,
2 DC, 3 down-left, 4 down-right (AVS 8×8, using the [1 2 1]-filtered edge for DC and the diagonals).
Chroma modes: 0 DC, 1 horizontal, 2 vertical, 3 plane. **(quirk)** chroma *horizontal* predicts
`pred[y][x] = left[x]` (indexed by column). Each block is reconstructed before the next is predicted.

### Inter MB (P 16x16/16x8/8x16, B 16x16)
```
se(mvd_x) se(mvd_y)                first partition
for each luma block in order:      16x8: raster; 8x16: (0,0) (0,1) (1,0) (1,1)
    [before the 3rd block, 16x8/8x16 only: se(mvd_x) se(mvd_y) of the second partition]
    1 bit coded [+ inter luma coefficients]
U, V: 1 bit coded [+ chroma coefficients]
```
B direct: four luma `coded` bits/coefficients and U, V as above, with no MVD.

## Coefficients

8×8 blocks, zig-zag scan. (level, run) pairs are sent **from the last coefficient backwards**, each through a
context-selected VLC, ending with EOB unless all 64 positions are used. Context starts at 0 (EOB impossible there)
and becomes `min(|level|, N)`.

| class | contexts (N) | sign | escape |
|---|---|---|---|
| intra luma | 7 (6) | 1 bit after the code (1 = negative) | `ue(|L|-1)`, `ue(run - maxrun[ctx][min(|L|,34)])`, sign |
| inter luma | 4 (3) | 1 bit after the code | `ue(|L|-1)`, `ue(run - maxrun[ctx][min(|L|,13)])`, sign |
| chroma (intra and inter) | 4 (3) | inside the code index | `ue(|L|-1)`, sign, `ue(run - maxrun[ctx][2*min(|L|,8)+s])` |

Symbol index → (level, run): for each level `L` (and sign `s` for chroma) the runs `0..maxrun-1` occupy
consecutive indices starting at `base`. Index 0 is EOB for contexts ≥ 1. Exact layouts are in `Tables.cs`
(note the chroma `base` table has a row stride of 15 while `maxrun` uses 16, and lookups may run into the next row;
both are reproduced as flat arrays).

**Dequantisation (quirk in scale):** `c' = (c * DequantMul[qp] + (1 << (shift-2))) >> (shift-1)`,
`shift = DequantShift[qp]` (AVS tables), then the **AVS 8×8 inverse transform**
(rows `(x+4)>>3`, columns clipped to [-32768, 32703] then `(x+64)>>7`). Chroma uses the luma QP.

## Motion

Quarter-pel MVs. Prediction per 8×8 block from left (A), above (B) and above-right (C) using the AVS
distance-median rule (the pair with the median L1 distance picks the third vector); if C is unavailable but A and B
are, top-left (D) values are used. Skip MV is zero if A or B is unavailable or zero, otherwise the 16×16 prediction.

Quirks reproduced exactly:
- C's availability is checked at `(x+1, y-1)` but its vector is read from `(x+2, y-1)` (block units, flat
  row-major indexing, so the last column reads the start of the next row). For the lower half of a 16×8 MB the row
  used is `y-2`.
- The per-block reference store never writes the bottom-left block of a macroblock, so that block always looks
  "available" (inter, ref 0) to later predictions even when the MB was intra.
- During 16×8/8×16 decoding, the first partition's vector is stored before predicting the second.

Chroma vectors: `(mv+1)>>1`; for 16×8/8×16, `(((a+1+b)>>1)+1)>>1` of the two partitions.

**Interpolation:** AVS filters — half-pel `[-1 5 5 -1]/8`, centre `J/64`, quarter-pel as weighted combinations
(see `Interp` in `Decoder.cs`). Chroma uses the same quarter-pel filters on the chroma plane. References are
edge-extended.

**B direct / skip:** for each 8×8 block take the co-located vector `c` of the future anchor;
`fwd = (c+1)>>1` (applied to the past anchor), `bwd = fwd - c` (applied to the future anchor); prediction is
`(p0 + p1 + 1) >> 1`. **(quirk)** chroma uses block 3's vectors, halved. Stored motion for direct/skip MBs is the
backward vector with ref 1.

## Loop filter

AVS deblocking on every picture after reconstruction (alpha/beta/C tables indexed by `clip(QP, 0, 64)`,
chroma by `ChromaQp[QP]`). Per MB in raster order: left MB edge, internal vertical edge (luma only), top MB edge
(skipped at slice tops), internal horizontal edge. Boundary strength (quirk):

- 2 if either side is intra, or the picture is I;
- otherwise compare the *current* block's MV with the co-located MV of the reference picture's motion field
  (P: the reference; B: future and past anchors), plus whether the reference's blocks on each side are intra:
  0 if all differences are < 4 quarter-pels and the intra status matches, else 1.

**(quirk)** for chroma, the strong-filter decision uses the first segment's strength for all eight lines.
