using System;
using System.Collections.Generic;

namespace Cvc1 {

/// <summary>A decoded 4:2:0 picture plus the per-8x8-block motion info used for prediction and deblocking.</summary>
public sealed class Picture {
    public readonly int W, H, CW, CH;
    public readonly byte[] Y, U, V;
    public int Type = -1;            // 0 = I, 1 = P, 2 = B, 3 = P referencing the last I/type-3 picture
    public readonly int[] BMode, BMvx, BMvy, BRef; // flat, row-major, padded (the format reads slightly past row ends)
    public Picture(int w, int h) {
        W = w; H = h; CW = w / 2; CH = h / 2;
        Y = new byte[w * h]; U = new byte[CW * CH]; V = new byte[CW * CH];
        int n = (w / 8) * (h / 8) + 2 * (w / 8) + 16;
        BMode = new int[n]; BMvx = new int[n]; BMvy = new int[n]; BRef = new int[n];
    }
}

/// <summary>
/// Decoder for the "CVC1" video codec written by ManyCam 2.5.x (2010). The format is a variant of
/// AVS1-P2 (Jizhun): 8x8 integer transform, AVS intra prediction, context-adaptive 2D VLC, quarter-pel
/// motion compensation and the AVS loop filter, with ManyCam's own headers and macroblock syntax.
/// Several quirks of ManyCam's encoder are reproduced on purpose so that pictures match what it intended.
/// See FORMAT.md.
/// </summary>
public sealed class Decoder {
    readonly CoefTables T = new CoefTables();
    public int W, H, MbW, MbH, Bw, Bh;
    int sliceRowsBlk = 1 << 20;        // slice height in 8x8-block rows
    int[] modeGrid;                    // intra prediction modes with a 1-block border
    public Picture LastAnchor, PrevAnchor, LastGolden;
    public bool Deblocking = true;

    static int Clip255(int v) { return v < 0 ? 0 : (v > 255 ? 255 : v); }
    static int Clip3(int lo, int hi, int v) { return v < lo ? lo : (v > hi ? hi : v); }

    // =====================================================================  frame / slice layer
    struct Slice { public int Start, DataStart, DataLen, StartRow, Type, Qp, Flag; }

    /// <summary>Decode one compressed frame (one AVI video chunk). Returns the reconstructed picture (decode order).</summary>
    public Picture DecodeFrame(byte[] f) {
        int p = 0;
        if (f.Length >= 12 && f[0] == 0xFF && f[1] == 0xFF && f[2] == 0xFF && f[3] == 0x08) {
            int w = (f[4] << 8) | f[5], h = (f[6] << 8) | f[7];
            if (w <= 0 || h <= 0 || (w & 15) != 0 || (h & 15) != 0) throw new NotSupportedException("unsupported frame size " + w + "x" + h);
            if (w != W || h != H) { W = w; H = h; MbW = w / 16; MbH = h / 16; Bw = w / 8; Bh = h / 8; LastAnchor = PrevAnchor = LastGolden = null; }
            p = 12;
        }
        if (W == 0) throw new InvalidOperationException("no sequence header seen yet");

        var slices = new List<Slice>();
        while (p + 4 <= f.Length) {
            if (!(f[p] == 0xFF && f[p + 1] == 0xFF && f[p + 2] == 0xFF)) throw new InvalidOperationException("missing start code");
            int code = f[p + 3];
            if (code == 0x0A) break;                       // end of frame
            if (code < 0x40 || code >= 0x80) throw new InvalidOperationException("unexpected start code " + code.ToString("X2"));
            var s = new Slice { Start = p, StartRow = code & 0x3F };
            int q = p + 4, len = (f[q] << 8) | f[q + 1]; q += 2;
            if (len == 0xFFFF) { len = (f[q] << 16) | (f[q + 1] << 8) | f[q + 2]; q += 3; s.DataLen = len - 7; } else s.DataLen = len - 4;
            s.Type = f[q] >> 6; s.Qp = f[q] & 0x3F; s.Flag = f[q + 1] >> 7; q += 2;
            s.DataStart = q;
            if (s.DataLen < 0 || q + s.DataLen > f.Length) throw new InvalidOperationException("truncated slice");
            slices.Add(s);
            p = q + s.DataLen;
        }
        if (slices.Count == 0) throw new InvalidOperationException("no slices");
        sliceRowsBlk = slices.Count > 1 ? Math.Max(1, slices[1].StartRow - slices[0].StartRow) : 1 << 20;

        int type = slices[0].Type, qp = slices[0].Qp;
        Picture refP = null;
        if (type == 1) refP = LastAnchor; else if (type == 3) refP = LastGolden;
        if ((type == 1 || type == 3) && refP == null) throw new InvalidOperationException("P frame without reference");
        if (type == 2 && (LastAnchor == null || PrevAnchor == null)) throw new InvalidOperationException("B frame without references");

        var pic = new Picture(W, H) { Type = type };
        modeGrid = new int[(Bw + 2) * (Bh + 2)];
        for (int i = 0; i < modeGrid.Length; i++) modeGrid[i] = -1;

        for (int si = 0; si < slices.Count; si++) {
            var s = slices[si];
            int mb0 = (s.StartRow / 2) * MbW;
            int mb1 = si + 1 < slices.Count ? (slices[si + 1].StartRow / 2) * MbW : MbW * MbH;
            var br = new BitReader(f, s.DataStart, s.DataStart + s.DataLen);
            if (s.Type == 0) {
                for (int mb = mb0; mb < mb1; mb++) { DecodeIntraMb(br, pic, mb % MbW, mb / MbW, s.Qp); StoreMbInfo(pic, mb % MbW, mb / MbW, 7, null, null, -1); }
            } else if (s.Type == 2) DecodeBSlice(br, pic, PrevAnchor, LastAnchor, mb0, mb1, s.Qp, s.Flag);
            else DecodePSlice(br, pic, refP, mb0, mb1, s.Qp);
        }

        if (Deblocking) {
            if (type == 0) Deblock(pic, null, null, 0, qp);
            else if (type == 1) Deblock(pic, refP, pic, 1, qp);
            else if (type == 3) Deblock(pic, refP, null, 3, qp);
            else Deblock(pic, LastAnchor, PrevAnchor, 2, qp);
        }
        if (type != 2) { PrevAnchor = LastAnchor; LastAnchor = pic; }
        if (type == 0 || type == 3) LastGolden = pic;
        return pic;
    }

    // =====================================================================  coefficients
    readonly int[] pairL = new int[65], pairR = new int[65];
    readonly short[][] coef = { new short[64], new short[64], new short[64], new short[64], new short[64], new short[64] };
    readonly bool[] coded = new bool[6];

    void Place(short[] blk, int n) {
        int pos = -1;
        for (int k = n - 1; k >= 0; k--) {           // pairs are sent highest frequency first
            pos += pairR[k] + 1;
            if (pos > 63) throw new InvalidOperationException("coefficient overflow");
            blk[Tbl.Zigzag[pos]] = (short)pairL[k];
        }
    }
    void ParseIntraLuma(BitReader br, short[] blk) {
        Array.Clear(blk, 0, 64); int n = 0, ctx = 0;
        while (n < 64) {
            int idx = T.IntraVlc[ctx].Decode(br);
            if (ctx > 0 && idx == 0) break;          // EOB
            int absL, run;
            if (idx == Tbl.IntraLumaEsc[ctx]) { absL = br.Ue() + 1; run = br.Ue() + Tbl.IntraLumaMaxRun[ctx * 34 + (absL > 33 ? 34 : absL)]; }
            else { absL = T.IntraL[ctx, idx]; run = T.IntraRun[ctx, idx]; if (absL < 0) throw new InvalidOperationException("bad intra symbol"); }
            pairL[n] = br.Bit() == 1 ? -absL : absL; pairR[n] = run; n++;
            ctx = absL < 6 ? absL : 6;
        }
        Place(blk, n);
    }
    void ParseInterLuma(BitReader br, short[] blk) {
        Array.Clear(blk, 0, 64); int n = 0, ctx = 0;
        while (n < 64) {
            int idx = T.InterVlc[ctx].Decode(br);
            if (ctx > 0 && idx == 0) break;
            int absL, run;
            if (idx == Tbl.InterLumaEsc[ctx]) { absL = br.Ue() + 1; run = br.Ue() + Tbl.InterLumaMaxRun[ctx * 13 + (absL > 12 ? 13 : absL)]; }
            else { absL = T.InterL[ctx, idx]; run = T.InterRun[ctx, idx]; if (absL < 0) throw new InvalidOperationException("bad inter symbol"); }
            pairL[n] = br.Bit() == 1 ? -absL : absL; pairR[n] = run; n++;
            ctx = absL < 3 ? absL : 3;
        }
        Place(blk, n);
    }
    void ParseChroma(BitReader br, short[] blk) {
        Array.Clear(blk, 0, 64); int n = 0, ctx = 0;
        while (n < 64) {
            int idx = T.ChromaVlc[ctx].Decode(br);
            if (ctx > 0 && idx == 0) break;
            int L, run;
            if (idx == Tbl.ChromaEsc[ctx]) {
                int absL = br.Ue() + 1, neg = br.Bit(), s = neg == 1 ? 0 : 1, lc = absL > 7 ? 8 : absL;
                run = br.Ue() + Tbl.ChromaMaxRun[ctx * 16 + 2 * lc + s]; L = neg == 1 ? -absL : absL;
            } else { L = T.ChromaL[ctx, idx]; run = T.ChromaRun[ctx, idx]; if (run < 0) throw new InvalidOperationException("bad chroma symbol"); }
            pairL[n] = L; pairR[n] = run; n++;
            int a = L < 0 ? -L : L; ctx = a < 3 ? a : 3;
        }
        Place(blk, n);
    }

    // =====================================================================  dequantisation + inverse transform (AVS 8x8)
    void DequantIdct(int qp, short[] r) {
        int mul = Tbl.DequantMul[qp], sh = Tbl.DequantShift[qp], rnd = 1 << (sh - 2), s1 = sh - 1;
        for (int y = 0; y < 8; y++) {
            int o = y * 8;
            int c0 = (r[o] * mul + rnd) >> s1, c1 = (r[o + 1] * mul + rnd) >> s1, c2 = (r[o + 2] * mul + rnd) >> s1, c3 = (r[o + 3] * mul + rnd) >> s1;
            int c4 = (r[o + 4] * mul + rnd) >> s1, c5 = (r[o + 5] * mul + rnd) >> s1, c6 = (r[o + 6] * mul + rnd) >> s1, c7 = (r[o + 7] * mul + rnd) >> s1;
            int a1 = c1 * 3 - c7 * 2, a2 = c3 * 3 + c5 * 2, a3 = (c3 - c5) * 2 - c5, a4 = c7 + (c1 + c7) * 2;
            int b0 = a2 + (a1 + a2 + a4) * 2, b1 = a1 * 3 - a2 * 2 + a3 * 2, b2 = (-a3 - a2) * 2 + a4 * 3, b3 = a1 * 2 - a3 * 3 - a4 * 2;
            int t = c2 * 10 + c6 * 4, u = c2 * 4 - c6 * 10, v = (c0 + c4) * 8, w = (c0 - c4) * 8;
            int e0 = v + t, e1 = w + u, e2 = w - u, e3 = v - t;
            r[o] = (short)((e0 + 4 + b0) >> 3); r[o + 1] = (short)((e1 + 4 + b1) >> 3); r[o + 2] = (short)((e2 + 4 + b2) >> 3); r[o + 3] = (short)((e3 + 4 + b3) >> 3);
            r[o + 7] = (short)((e0 - b0 + 4) >> 3); r[o + 6] = (short)((e1 - b1 + 4) >> 3); r[o + 5] = (short)((e2 - b2 + 4) >> 3); r[o + 4] = (short)((e3 - b3 + 4) >> 3);
        }
        for (int x = 0; x < 8; x++) {
            int c0 = r[x], c1 = r[8 + x], c2 = r[16 + x], c3 = r[24 + x], c4 = r[32 + x], c5 = r[40 + x], c6 = r[48 + x], c7 = r[56 + x];
            int a1 = c1 * 3 - c7 * 2, a2 = c3 * 3 + c5 * 2, a3 = (c3 - c5) * 2 - c5, a4 = c7 + (c1 + c7) * 2;
            int b0 = a2 + (a1 + a2 + a4) * 2, b1 = a1 * 3 - a2 * 2 + a3 * 2, b2 = (-a3 - a2) * 2 + a4 * 3, b3 = a1 * 2 - a3 * 3 - a4 * 2;
            int t = c2 * 10 + c6 * 4, u = c2 * 4 - c6 * 10, v = (c0 + c4) * 8, w = (c0 - c4) * 8;
            int e0 = v + t, e1 = w + u, e2 = w - u, e3 = v - t;
            r[x] = Out(e0 + b0); r[8 + x] = Out(e1 + b1); r[16 + x] = Out(e2 + b2); r[24 + x] = Out(e3 + b3);
            r[56 + x] = Out(e0 - b0); r[48 + x] = Out(e1 - b1); r[40 + x] = Out(e2 - b2); r[32 + x] = Out(e3 - b3);
        }
    }
    static short Out(int v) { if (v < -0x8000) v = -0x8000; else if (v >= 0x7fc0) v = 0x7fbf; return (short)((v + 0x40) >> 7); }

    // =====================================================================  intra prediction (AVS)
    readonly byte[] E = new byte[48];
    readonly short[] pred = new short[64];
    int GetMode(int bx, int by) { return modeGrid[(by + 1) * (Bw + 2) + 1 + bx]; }
    void SetMode(int bx, int by, int m) { modeGrid[(by + 1) * (Bw + 2) + 1 + bx] = m; }

    void LumaAvail(int bx, int by, out bool top, out bool tr, out bool left, out bool dl) {
        top = by >= 1;
        tr = (bx + 1 < Bw && by >= 1 && (by & 1) == 0) || ((bx & 1) == 0 && (by & 1) == 1);
        left = bx >= 1;
        dl = bx >= 1 && (bx & 1) == 0 && (by & 1) == 0 && (by - 1) < Bh - 2;
        if (by > 0 && (by - 1) / sliceRowsBlk < by / sliceRowsBlk) { top = false; tr = false; }
    }
    void LumaEdges(byte[] pl, int s, int o, bool top, bool tr, bool left, bool dl) {
        Array.Clear(E, 0, E.Length);
        if (top) {
            for (int i = 0; i < 8; i++) E[0x15 + i] = pl[o - s + i];
            for (int i = 0; i < 8; i++) E[0x1d + i] = tr ? pl[o - s + 8 + i] : E[0x1c];
            E[0x25] = E[0x24]; E[0x26] = E[0x25]; E[0x14] = pl[o - s];
        }
        if (left) {
            for (int i = 0; i < 8; i++) E[0x13 - i] = pl[o - 1 + i * s];
            for (int i = 0; i < 8; i++) E[0x0b - i] = dl ? pl[o - 1 + (8 + i) * s] : E[0x0c];
            E[3] = E[4]; E[2] = E[3]; E[0x14] = pl[o - 1];
        }
        if (top && left) E[0x14] = pl[o - s - 1];
        int prev = E[4];
        for (int i = 4; i <= 0x24; i++) { int cur = E[i]; E[i] = (byte)((prev + cur * 2 + 2 + E[i + 1]) >> 2); prev = cur; }
    }
    void LumaPred(byte[] pl, int s, int o, bool top, bool left, int mode) {
        for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++) {
            int v;
            switch (mode) {
                case 0: v = pl[o - s + x]; break;                                    // vertical (unfiltered)
                case 1: v = pl[o - 1 + y * s]; break;                                // horizontal (unfiltered)
                case 2: v = top && left ? (E[0x15 + x] + E[0x13 - y]) >> 1 : top ? E[0x15 + x] : left ? E[0x13 - y] : 128; break;
                case 3: v = Math.Min(255, (E[0x16 + x + y] + E[0x12 - x - y]) >> 1); break; // down-left
                case 4: v = E[0x14 + x - y]; break;                                  // down-right
                default: throw new InvalidOperationException("bad intra mode");
            }
            pred[y * 8 + x] = (short)v;
        }
    }
    void ChromaEdges(byte[] pl, int s, int o, bool top, bool left, bool tl) {
        Array.Clear(E, 0, E.Length);
        if (top) { for (int i = 0; i < 8; i++) E[0x15 + i] = pl[o - s + i]; for (int i = 0; i < 8; i++) E[0x1d + i] = E[0x1c]; E[0x14] = pl[o - s]; }
        if (left) { for (int i = 0; i < 8; i++) E[0x13 - i] = pl[o - 1 + i * s]; for (int i = 0; i < 8; i++) E[0x0b - i] = E[0x0c]; E[0x14] = pl[o - 1]; }
        if (tl) E[0x14] = pl[o - 1 - s];
        int prev = E[0x0c];
        for (int i = 0x0c; i <= 0x1c; i++) { int cur = E[i]; E[i] = (byte)((prev + cur * 2 + 2 + E[i + 1]) >> 2); prev = cur; }
    }
    void ChromaPred(byte[] pl, int s, int o, bool top, bool left, int mode) {
        if (mode == 3) {                                                              // plane
            int tlp = pl[o - 1 - s], ih = (pl[o + 7 - s] - tlp) * 4, iv = (pl[o - 1 + 7 * s] - tlp) * 4;
            for (int i = 1; i < 4; i++) { ih += (pl[o - s + 3 + i] - pl[o - s + 3 - i]) * i; iv += (pl[o - 1 + (3 + i) * s] - pl[o - 1 + (3 - i) * s]) * i; }
            int ib = (ih * 17 + 16) >> 5, ic = (iv * 17 + 16) >> 5, ia = (pl[o + 7 - s] + pl[o - 1 + 7 * s]) * 16;
            for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++) pred[y * 8 + x] = (short)Clip255(((x - 3) * ib + ia + 16 + (y - 3) * ic) / 32);
            return;
        }
        for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++) {
            int v;
            if (mode == 0) v = top && left ? (E[0x15 + x] + E[0x13 - y]) >> 1 : top ? E[0x15 + x] : left ? E[0x13 - y] : 128;
            else if (mode == 1) v = pl[o - 1 + x * s];   // ManyCam's horizontal mode indexes the left edge by column (sic)
            else if (mode == 2) v = pl[o - s + x];
            else throw new InvalidOperationException("bad chroma mode");
            pred[y * 8 + x] = (short)v;
        }
    }
    void Recon(byte[] pl, int s, int o, short[] res, bool hasRes) {
        for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++) pl[o + y * s + x] = (byte)Clip255(pred[y * 8 + x] + (hasRes ? res[y * 8 + x] : 0));
    }

    void DecodeIntraMb(BitReader br, Picture pic, int mbx, int mby, int qp) {
        for (int b = 0; b < 4; b++) {
            int bx = mbx * 2 + (b & 1), by = mby * 2 + (b >> 1);
            int top = (by % sliceRowsBlk == 0) ? -1 : GetMode(bx, by - 1), left = GetMode(bx - 1, by);
            int mpm = (top < 0 || left < 0) ? 2 : Math.Min(top, left), m;
            if (br.Bit() == 1) m = mpm; else { int r = (int)br.Read(2); m = r < mpm ? r : r + 1; }
            SetMode(bx, by, m);
            coded[b] = br.Bit() == 1;
            if (coded[b]) ParseIntraLuma(br, coef[b]);
            bool at, atr, al, adl; LumaAvail(bx, by, out at, out atr, out al, out adl);
            int o = by * 8 * W + bx * 8;
            LumaEdges(pic.Y, W, o, at, atr, al, adl);
            LumaPred(pic.Y, W, o, at, al, m);
            if (coded[b]) DequantIdct(qp, coef[b]);
            Recon(pic.Y, W, o, coef[b], coded[b]);
        }
        int cm = br.Ue();
        for (int c = 4; c < 6; c++) { coded[c] = br.Bit() == 1; if (coded[c]) ParseChroma(br, coef[c]); }
        bool ctop = mby >= 1 && (mby * 2) % sliceRowsBlk != 0, cleft = mbx >= 1, ctl = ctop && cleft;
        int co = mby * 8 * pic.CW + mbx * 8;
        for (int c = 4; c < 6; c++) {
            byte[] pl = c == 4 ? pic.U : pic.V;
            ChromaEdges(pl, pic.CW, co, ctop, cleft, ctl);
            ChromaPred(pl, pic.CW, co, ctop, cleft, cm);
            if (coded[c]) DequantIdct(qp, coef[c]);
            Recon(pl, pic.CW, co, coef[c], coded[c]);
        }
    }

    // =====================================================================  motion info + MV prediction
    void StoreMbInfo(Picture p, int mbx, int mby, int mode, int[] mvx, int[] mvy, int refv) {
        int bx = mbx * 2, by = mby * 2;
        int i0 = by * Bw + bx, i1 = i0 + 1, i2 = i0 + Bw, i3 = i2 + 1;
        int[] idx = { i0, i1, i2, i3 };
        for (int i = 0; i < 4; i++) { p.BMode[idx[i]] = mode; p.BMvx[idx[i]] = mvx == null ? 0 : mvx[i]; p.BMvy[idx[i]] = mvy == null ? 0 : mvy[i]; }
        p.BRef[i0] = refv; p.BRef[i1] = refv; p.BRef[i3] = refv; // ManyCam never updates the bottom-left block's reference (sic)
    }
    static void TempStore(Picture p, int i, int mx, int my) { p.BMvx[i] = mx; p.BMvy[i] = my; p.BRef[i] = 0; p.BMode[i] = 1; }

    void MvPred(Picture p, int bx, int by, bool is16, out int pmx, out int pmy, out int skx, out int sky) {
        int wb = Bw; bool sliceTop = (by % sliceRowsBlk) == 0;
        bool aA = false, aB = false, aC = false;
        int ax = 0, ay = 0, bxv = 0, byv = 0, cx = 0, cy = 0;
        if (bx >= 1) aA = p.BRef[by * wb + bx - 1] != -1;
        if (by >= 1 && !sliceTop) aB = p.BRef[(by - 1) * wb + bx] != -1;
        if (!(wb - 1 < bx || by < 1 || sliceTop)) aC = p.BRef[(by - 1) * wb + bx + 1] != -1;
        if (aB) { int i = (by - 1) * wb + bx; bxv = p.BMvx[i]; byv = p.BMvy[i]; }
        if (aA) { int i = by * wb + bx - 1; ax = p.BMvx[i]; ay = p.BMvy[i]; }
        int ci = -1;
        if (!aC || wb - 1 < bx) {                                   // substitute D (top-left) values, flag stays 0
            if (aB && aA) ci = (is16 || (by & 1) == 0) ? (by - 1) * wb + bx - 1 : (by >= 2 ? (by - 2) * wb + bx - 1 : -1);
        } else {
            if (is16 || (by & 1) == 0) ci = (by - 1) * wb + bx + 2;
            else if (by < 2) aC = false; else ci = (by - 2) * wb + bx + 2;
        }
        if (ci >= 0) { cx = p.BMvx[ci]; cy = p.BMvy[ci]; }
        Median(aA, aB, aC, ax, ay, bxv, byv, cx, cy, out pmx, out pmy);
        if (!aB || (bxv == 0 && byv == 0) || !aA || (ax == 0 && ay == 0)) { skx = 0; sky = 0; } else { skx = pmx; sky = pmy; }
    }
    static void Median(bool aA, bool aB, bool aC, int ax, int ay, int bx, int by, int cx, int cy, out int px, out int py) {
        px = 0; py = 0;
        if (aA && !aB && !aC) { px = ax; py = ay; }
        else if (!aA && aB && !aC) { px = bx; py = by; }
        else if (!aA && !aB && aC) { px = cx; py = cy; }
        int n = (aA ? 1 : 0) + (aB ? 1 : 0) + (aC ? 1 : 0);
        if (n < 2) { if (n == 0) { px = 0; py = 0; } return; }
        int vab = Math.Abs(ax - bx) + Math.Abs(ay - by), vbc = Math.Abs(bx - cx) + Math.Abs(by - cy), vca = Math.Abs(cx - ax) + Math.Abs(cy - ay), med;
        if (vbc < vab) med = vca < vab ? (vca < vbc ? vbc : vca) : vab;
        else med = vca < vbc ? (vca < vab ? vab : vca) : vbc;
        if (med == vab) { px = cx; py = cy; } else if (med == vbc) { px = ax; py = ay; } else { px = bx; py = by; }
    }

    // =====================================================================  motion compensation (AVS quarter-pel)
    static int Px(byte[] r, int w, int h, int x, int y) { if (x < 0) x = 0; else if (x >= w) x = w - 1; if (y < 0) y = 0; else if (y >= h) y = h - 1; return r[y * w + x]; }
    static int Hh(byte[] r, int w, int h, int x, int y) { return 5 * Px(r, w, h, x, y) - Px(r, w, h, x - 1, y) + 5 * Px(r, w, h, x + 1, y) - Px(r, w, h, x + 2, y); }
    static int Hv(byte[] r, int w, int h, int x, int y) { return 5 * Px(r, w, h, x, y) - Px(r, w, h, x, y - 1) + 5 * Px(r, w, h, x, y + 1) - Px(r, w, h, x, y + 2); }
    static int J(byte[] r, int w, int h, int x, int y) { return 5 * Hh(r, w, h, x, y) - Hh(r, w, h, x, y - 1) + 5 * Hh(r, w, h, x, y + 1) - Hh(r, w, h, x, y + 2); }
    static int Interp(byte[] r, int w, int h, int x, int y, int fx, int fy) {
        switch (fy * 4 + fx) {
            case 0: return Px(r, w, h, x, y);
            case 2: return Clip255((Hh(r, w, h, x, y) + 4) >> 3);
            case 8: return Clip255((Hv(r, w, h, x, y) + 4) >> 3);
            case 10: return Clip255((J(r, w, h, x, y) + 32) >> 6);
            case 1: return Clip255((56 * Px(r, w, h, x, y) + 7 * Hh(r, w, h, x, y) + 8 * Px(r, w, h, x + 1, y) + Hh(r, w, h, x - 1, y) + 64) >> 7);
            case 3: return Clip255((56 * Px(r, w, h, x + 1, y) + 7 * Hh(r, w, h, x, y) + 8 * Px(r, w, h, x, y) + Hh(r, w, h, x + 1, y) + 64) >> 7);
            case 4: return Clip255((7 * Hv(r, w, h, x, y) + 56 * Px(r, w, h, x, y) + Hv(r, w, h, x, y - 1) + 8 * Px(r, w, h, x, y + 1) + 64) >> 7);
            case 12: return Clip255((56 * Px(r, w, h, x, y + 1) + 7 * Hv(r, w, h, x, y) + 8 * Px(r, w, h, x, y) + Hv(r, w, h, x, y + 1) + 64) >> 7);
            case 5: return Clip255((J(r, w, h, x, y) + 64 * Px(r, w, h, x, y) + 64) >> 7);
            case 7: return Clip255((J(r, w, h, x, y) + 64 * Px(r, w, h, x + 1, y) + 64) >> 7);
            case 13: return Clip255((J(r, w, h, x, y) + 64 * Px(r, w, h, x, y + 1) + 64) >> 7);
            case 15: return Clip255((J(r, w, h, x, y) + 64 * Px(r, w, h, x + 1, y + 1) + 64) >> 7);
            case 6: return Clip255((-Hh(r, w, h, x, y - 2) - 2 * Hh(r, w, h, x, y - 1) + 96 * Hh(r, w, h, x, y) + 42 * Hh(r, w, h, x, y + 1) - 7 * Hh(r, w, h, x, y + 2) + 512) >> 10);
            case 14: return Clip255((-7 * Hh(r, w, h, x, y - 1) + 42 * Hh(r, w, h, x, y) + 96 * Hh(r, w, h, x, y + 1) - 2 * Hh(r, w, h, x, y + 2) - Hh(r, w, h, x, y + 3) + 512) >> 10);
            case 9: return Clip255((-Hv(r, w, h, x - 2, y) - 2 * Hv(r, w, h, x - 1, y) + 96 * Hv(r, w, h, x, y) + 42 * Hv(r, w, h, x + 1, y) - 7 * Hv(r, w, h, x + 2, y) + 512) >> 10);
            default: return Clip255((-7 * Hv(r, w, h, x - 1, y) + 42 * Hv(r, w, h, x, y) + 96 * Hv(r, w, h, x + 1, y) - 2 * Hv(r, w, h, x + 2, y) - Hv(r, w, h, x + 3, y) + 512) >> 10); // case 11
        }
    }
    static void Mc8(byte[] r, int w, int h, int x0, int y0, int mvx, int mvy, int[] buf) {
        int ix = x0 + (mvx >> 2), iy = y0 + (mvy >> 2), fx = mvx & 3, fy = mvy & 3;
        if (fx == 0 && fy == 0 && ix >= 0 && iy >= 0 && ix + 8 <= w && iy + 8 <= h) { for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++) buf[y * 8 + x] = r[(iy + y) * w + ix + x]; return; }
        for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++) buf[y * 8 + x] = Interp(r, w, h, ix + x, iy + y, fx, fy);
    }
    readonly int[] mc0 = new int[64], mc1 = new int[64];
    void McRecon(byte[] refPl, byte[] dst, int w, int h, int x0, int y0, int mvx, int mvy, short[] res, bool hasRes) {
        Mc8(refPl, w, h, x0, y0, mvx, mvy, mc0);
        for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++) dst[(y0 + y) * w + x0 + x] = (byte)Clip255(mc0[y * 8 + x] + (hasRes ? res[y * 8 + x] : 0));
    }
    void BiRecon(byte[] a, byte[] b, byte[] dst, int w, int h, int x0, int y0, int m0x, int m0y, int m1x, int m1y, short[] res, bool hasRes) {
        Mc8(a, w, h, x0, y0, m0x, m0y, mc0); Mc8(b, w, h, x0, y0, m1x, m1y, mc1);
        for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++) dst[(y0 + y) * w + x0 + x] = (byte)Clip255((hasRes ? res[y * 8 + x] : 0) + ((mc0[y * 8 + x] + 1 + mc1[y * 8 + x]) >> 1));
    }
    void SetInterModes(int mbx, int mby) { for (int b = 0; b < 4; b++) SetMode(mbx * 2 + (b & 1), mby * 2 + (b >> 1), -1); }
    void ParseInterResidual(BitReader br, int[] order) {
        foreach (int b in order) { coded[b] = br.Bit() == 1; if (coded[b]) ParseInterLuma(br, coef[b]); }
        for (int c = 4; c < 6; c++) { coded[c] = br.Bit() == 1; if (coded[c]) ParseChroma(br, coef[c]); }
    }

    // =====================================================================  P slices
    void DecodePSlice(BitReader br, Picture cur, Picture refp, int mb, int mbEnd, int qp) {
        while (mb < mbEnd) {
            int run = br.Ue();
            for (int k = 0; k < run && mb < mbEnd; k++, mb++) DecodePSkip(cur, refp, mb % MbW, mb / MbW);
            if (mb >= mbEnd) break;
            int mbx = mb % MbW, mby = mb / MbW, type;
            if (br.Bit() == 0) type = 0; else if (br.Bit() == 0) type = 7; else if (br.Bit() == 0) type = 1; else if (br.Bit() == 0) type = 2;
            else throw new InvalidOperationException("bad P macroblock type");
            if (type == 7) { DecodeIntraMb(br, cur, mbx, mby, qp); StoreMbInfo(cur, mbx, mby, 7, null, null, -1); }
            else DecodePInter(br, cur, refp, mbx, mby, qp, type);
            mb++;
        }
    }
    void DecodePSkip(Picture cur, Picture refp, int mbx, int mby) {
        int pmx, pmy, sx, sy; MvPred(cur, mbx * 2, mby * 2, true, out pmx, out pmy, out sx, out sy);
        SetInterModes(mbx, mby);
        for (int b = 0; b < 4; b++) McRecon(refp.Y, cur.Y, W, H, mbx * 16 + (b & 1) * 8, mby * 16 + (b >> 1) * 8, sx, sy, null, false);
        int cx = (sx + 1) >> 1, cy = (sy + 1) >> 1;
        McRecon(refp.U, cur.U, cur.CW, cur.CH, mbx * 8, mby * 8, cx, cy, null, false);
        McRecon(refp.V, cur.V, cur.CW, cur.CH, mbx * 8, mby * 8, cx, cy, null, false);
        StoreMbInfo(cur, mbx, mby, 4, new[] { sx, sx, sx, sx }, new[] { sy, sy, sy, sy }, 0);
    }
    static readonly int[] OrderRaster = { 0, 1, 2, 3 }, OrderColumns = { 0, 2, 1, 3 };
    void DecodePInter(BitReader br, Picture cur, Picture refp, int mbx, int mby, int qp, int type) {
        int bx0 = mbx * 2, by0 = mby * 2, i00 = by0 * Bw + bx0;
        int[] mvx = new int[4], mvy = new int[4];
        int pmx, pmy, sx, sy;
        MvPred(cur, bx0, by0, type == 0, out pmx, out pmy, out sx, out sy);
        int mx = br.Se() + pmx, my = br.Se() + pmy;
        int[] order = type == 2 ? OrderColumns : OrderRaster;
        if (type == 0) for (int i = 0; i < 4; i++) { mvx[i] = mx; mvy[i] = my; }
        else if (type == 1) { mvx[0] = mvx[1] = mx; mvy[0] = mvy[1] = my; }
        else { mvx[0] = mvx[2] = mx; mvy[0] = mvy[2] = my; }
        SetInterModes(mbx, mby);
        for (int k = 0; k < 4; k++) {
            if (k == 2 && type != 0) {   // second partition's MVD sits in the middle of the MB data
                if (type == 1) { TempStore(cur, i00, mx, my); TempStore(cur, i00 + 1, mx, my); MvPred(cur, bx0, by0 + 1, false, out pmx, out pmy, out sx, out sy); }
                else { TempStore(cur, i00, mx, my); TempStore(cur, i00 + Bw, mx, my); MvPred(cur, bx0 + 1, by0, false, out pmx, out pmy, out sx, out sy); }
                int m2x = br.Se() + pmx, m2y = br.Se() + pmy;
                if (type == 1) { mvx[2] = mvx[3] = m2x; mvy[2] = mvy[3] = m2y; } else { mvx[1] = mvx[3] = m2x; mvy[1] = mvy[3] = m2y; }
            }
            int b = order[k]; coded[b] = br.Bit() == 1; if (coded[b]) ParseInterLuma(br, coef[b]);
        }
        for (int c = 4; c < 6; c++) { coded[c] = br.Bit() == 1; if (coded[c]) ParseChroma(br, coef[c]); }
        for (int b = 0; b < 4; b++) {
            if (coded[b]) DequantIdct(qp, coef[b]);
            McRecon(refp.Y, cur.Y, W, H, mbx * 16 + (b & 1) * 8, mby * 16 + (b >> 1) * 8, mvx[b], mvy[b], coef[b], coded[b]);
        }
        int cmx, cmy;
        if (type == 0) { cmx = (mvx[0] + 1) >> 1; cmy = (mvy[0] + 1) >> 1; }
        else { int o = type == 1 ? 2 : 1; cmx = (((mvx[0] + 1 + mvx[o]) >> 1) + 1) >> 1; cmy = (((mvy[0] + 1 + mvy[o]) >> 1) + 1) >> 1; }
        for (int c = 4; c < 6; c++) {
            if (coded[c]) DequantIdct(qp, coef[c]);
            McRecon(c == 4 ? refp.U : refp.V, c == 4 ? cur.U : cur.V, cur.CW, cur.CH, mbx * 8, mby * 8, cmx, cmy, coef[c], coded[c]);
        }
        StoreMbInfo(cur, mbx, mby, type, mvx, mvy, 0);
    }

    // =====================================================================  B slices
    int MbQpB(int baseQp, int mbx, int mby) {
        if (mbx < 2 || mby < 2 || Bh / 2 - mby < 2 || Bw / 2 - mbx < 2) return baseQp + 2;
        if (Math.Abs(Bw / 4 - mbx) > 3 && Math.Abs(Bh / 4 - mby) > 2) return baseQp;
        return baseQp - 2;
    }
    void DecodeBSlice(BitReader br, Picture cur, Picture past, Picture future, int mb, int mbEnd, int qp, int flag) {
        while (mb < mbEnd) {
            int run = br.Ue();
            for (int k = 0; k < run && mb < mbEnd; k++, mb++) DecodeBDirect(br, cur, past, future, mb % MbW, mb / MbW, qp, false);
            if (mb >= mbEnd) break;
            int mbx = mb % MbW, mby = mb / MbW, type;
            if (br.Bit() == 0) type = 5; else if (br.Bit() == 0) type = 0; else if (br.Bit() == 0) type = 7;
            else throw new InvalidOperationException("bad B macroblock type");
            int mbqp = qp;
            if (type == 7) {
                if (flag == 1) mbqp = MbQpB(qp, mbx, mby);
                DecodeIntraMb(br, cur, mbx, mby, mbqp); StoreMbInfo(cur, mbx, mby, 7, null, null, -1);
            } else {
                if (flag == 1 && br.Bit() == 1) mbqp = br.Bit() == 1 ? qp - 2 : qp + 2;
                if (type == 5) DecodeBDirect(br, cur, past, future, mbx, mby, mbqp, true);
                else DecodeB16(br, cur, past, future, mbx, mby, mbqp);
            }
            mb++;
        }
    }
    void DecodeBDirect(BitReader br, Picture cur, Picture past, Picture future, int mbx, int mby, int qp, bool withResidual) {
        int[] fx = new int[4], fy = new int[4], bx = new int[4], by = new int[4];
        for (int b = 0; b < 4; b++) {     // temporal direct: halve the co-located MV of the future anchor
            int i = (mby * 2 + (b >> 1)) * Bw + mbx * 2 + (b & 1), cx = future.BMvx[i], cy = future.BMvy[i];
            fx[b] = (cx + 1) >> 1; fy[b] = (cy + 1) >> 1; bx[b] = fx[b] - cx; by[b] = fy[b] - cy;
        }
        if (withResidual) ParseInterResidual(br, OrderRaster); else for (int c = 0; c < 6; c++) coded[c] = false;
        SetInterModes(mbx, mby);
        for (int b = 0; b < 4; b++) {
            if (coded[b]) DequantIdct(qp, coef[b]);
            BiRecon(past.Y, future.Y, cur.Y, W, H, mbx * 16 + (b & 1) * 8, mby * 16 + (b >> 1) * 8, fx[b], fy[b], bx[b], by[b], coef[b], coded[b]);
        }
        int cfx = (fx[3] + 1) >> 1, cfy = (fy[3] + 1) >> 1, cbx = (bx[3] + 1) >> 1, cby = (by[3] + 1) >> 1; // chroma uses block 3 (sic)
        for (int c = 4; c < 6; c++) {
            if (coded[c]) DequantIdct(qp, coef[c]);
            BiRecon(c == 4 ? past.U : past.V, c == 4 ? future.U : future.V, c == 4 ? cur.U : cur.V, cur.CW, cur.CH, mbx * 8, mby * 8, cfx, cfy, cbx, cby, coef[c], coded[c]);
        }
        StoreMbInfo(cur, mbx, mby, withResidual ? 5 : 4, bx, by, 1);
    }
    void DecodeB16(BitReader br, Picture cur, Picture past, Picture future, int mbx, int mby, int qp) {
        int dir = br.Bit(); Picture refp = dir == 0 ? future : past;
        int pmx, pmy, sx, sy; MvPred(cur, mbx * 2, mby * 2, true, out pmx, out pmy, out sx, out sy);
        int mx = br.Se() + pmx, my = br.Se() + pmy;
        ParseInterResidual(br, OrderRaster);
        SetInterModes(mbx, mby);
        for (int b = 0; b < 4; b++) {
            if (coded[b]) DequantIdct(qp, coef[b]);
            McRecon(refp.Y, cur.Y, W, H, mbx * 16 + (b & 1) * 8, mby * 16 + (b >> 1) * 8, mx, my, coef[b], coded[b]);
        }
        int cmx = (mx + 1) >> 1, cmy = (my + 1) >> 1;
        for (int c = 4; c < 6; c++) {
            if (coded[c]) DequantIdct(qp, coef[c]);
            McRecon(c == 4 ? refp.U : refp.V, c == 4 ? cur.U : cur.V, cur.CW, cur.CH, mbx * 8, mby * 8, cmx, cmy, coef[c], coded[c]);
        }
        StoreMbInfo(cur, mbx, mby, 0, new[] { mx, mx, mx, mx }, new[] { my, my, my, my }, dir);
    }

    // =====================================================================  loop filter (AVS, with ManyCam's boundary-strength rule)
    static int Bs(Picture g10, Picture g11, Picture g12, int ftype, int P, int Q) {
        if (g10.BMode[P] == 7 || g10.BMode[Q] == 7 || ftype == 0) return 2;
        int g11P = g11 != null ? g11.BMode[P] : -1, g11Q = g11 != null ? g11.BMode[Q] : -1;
        int g12P = g12 != null ? g12.BMode[P] : -1, g12Q = g12 != null ? g12.BMode[Q] : -1;
        int d1x = Math.Abs(g10.BMvx[Q] - (g11 != null ? g11.BMvx[Q] : 0)), d1y = Math.Abs(g10.BMvy[Q] - (g11 != null ? g11.BMvy[Q] : 0));
        int d2x = Math.Abs(g10.BMvx[Q] - (g12 != null ? g12.BMvx[Q] : 0)), d2y = Math.Abs(g10.BMvy[Q] - (g12 != null ? g12.BMvy[Q] : 0));
        if (ftype == 1) return ((g11P == 7) == (g11Q == 7) && d1x < 4 && d1y < 4) ? 0 : 1;
        return ((g11P == 7) == (g11Q == 7) && (g12P == 7) == (g12Q == 7) && d1x < 4 && d1y < 4 && d2x < 4 && d2y < 4) ? 0 : 1;
    }
    void Deblock(Picture cur, Picture g11, Picture g12, int ftype, int qp) {
        int[] bs = new int[2];
        int sliceMb = Math.Max(1, sliceRowsBlk / 2), cqp = Tbl.LoopChromaQp[Clip3(0, 63, qp)];
        for (int mby = 0; mby < MbH; mby++) for (int mbx = 0; mbx < MbW; mbx++) {
            int bx = mbx * 2, by = mby * 2, r0 = by * Bw + bx, r1 = r0 + Bw;
            if (mbx != 0) {
                bs[0] = Bs(cur, g11, g12, ftype, r0 - 1, r0); bs[1] = Bs(cur, g11, g12, ftype, r1 - 1, r1);
                LumaEdge(cur.Y, W, mbx, mby, 0, false, bs, qp);
                ChromaEdge(cur.U, cur.CW, mbx, mby, false, bs, cqp); ChromaEdge(cur.V, cur.CW, mbx, mby, false, bs, cqp);
            }
            bs[0] = Bs(cur, g11, g12, ftype, r0, r0 + 1); bs[1] = Bs(cur, g11, g12, ftype, r1, r1 + 1);
            LumaEdge(cur.Y, W, mbx, mby, 8, false, bs, qp);
            if (mby != 0 && mby % sliceMb != 0) {
                bs[0] = Bs(cur, g11, g12, ftype, r0 - Bw, r0); bs[1] = Bs(cur, g11, g12, ftype, r0 - Bw + 1, r0 + 1);
                LumaEdge(cur.Y, W, mbx, mby, 0, true, bs, qp);
                ChromaEdge(cur.U, cur.CW, mbx, mby, true, bs, cqp); ChromaEdge(cur.V, cur.CW, mbx, mby, true, bs, cqp);
            }
            bs[0] = Bs(cur, g11, g12, ftype, r0, r1); bs[1] = Bs(cur, g11, g12, ftype, r0 + 1, r1 + 1);
            LumaEdge(cur.Y, W, mbx, mby, 8, true, bs, qp);
        }
    }
    static void LumaEdge(byte[] pl, int stride, int mbx, int mby, int off, bool horizontal, int[] bs, int qp) {
        int across = horizontal ? stride : 1, along = horizontal ? 1 : stride, p = mbx * 16 + mby * 16 * stride + off * across;
        int qi = Clip3(0, 64, qp), alpha = Tbl.LoopAlpha[qi], beta = Tbl.LoopBeta[qi], cl = Tbl.LoopClip[qi];
        for (int line = 0; line < 16; line++, p += along) {
            int s = bs[line >> 3]; if (s == 0) continue;
            int q0 = pl[p], q1 = pl[p + across], q2 = pl[p + 2 * across], p0 = pl[p - across], p1 = pl[p - 2 * across], p2 = pl[p - 3 * across];
            int d0 = Math.Abs(q0 - p0);
            if (!(d0 < alpha && Math.Abs(q0 - q1) < beta && Math.Abs(p0 - p1) < beta)) continue;
            int aq = Math.Abs(q0 - q2), ap = Math.Abs(p0 - p2);
            if (s == 2) {
                bool small = d0 < (alpha >> 2) + 2;
                if (aq < beta && small) { pl[p] = (byte)((q1 + q0 * 2 + 2 + p0) >> 2); pl[p + across] = (byte)((q1 * 2 + q0 + 2 + p0) >> 2); }
                else pl[p] = (byte)((q1 * 2 + q0 + 2 + p0) >> 2);
                if (ap < beta && small) { pl[p - across] = (byte)((p1 + p0 * 2 + 2 + q0) >> 2); pl[p - 2 * across] = (byte)((p1 * 2 + p0 + 2 + q0) >> 2); }
                else pl[p - across] = (byte)((p1 * 2 + p0 + 2 + q0) >> 2);
            } else {
                int d = Clip3(-cl, cl, ((q0 - p0) * 3 + 4 + (p1 - q1)) >> 3), np0 = Clip255(p0 + d), nq0 = Clip255(q0 - d);
                pl[p] = (byte)nq0; pl[p - across] = (byte)np0;
                if (ap < beta) pl[p - 2 * across] = (byte)Clip255(p1 + Clip3(-cl, cl, ((np0 - p1) * 3 + 4 + (p2 - nq0)) >> 3));
                if (aq < beta) pl[p + across] = (byte)Clip255(q1 - Clip3(-cl, cl, ((q1 - nq0) * 3 + 4 + (np0 - q2)) >> 3));
            }
        }
    }
    static void ChromaEdge(byte[] pl, int stride, int mbx, int mby, bool horizontal, int[] bs, int cqp) {
        int across = horizontal ? stride : 1, along = horizontal ? 1 : stride, p = mbx * 8 + mby * 8 * stride;
        int qi = Clip3(0, 64, cqp), alpha = Tbl.LoopAlpha[qi], beta = Tbl.LoopBeta[qi], cl = Tbl.LoopClip[qi];
        for (int line = 0; line < 8; line++, p += along) {
            if (bs[line >> 2] == 0) continue;
            int q0 = pl[p], q1 = pl[p + across], q2 = pl[p + 2 * across], p0 = pl[p - across], p1 = pl[p - 2 * across], p2 = pl[p - 3 * across];
            int d0 = Math.Abs(q0 - p0);
            if (!(d0 < alpha && Math.Abs(q0 - q1) < beta && Math.Abs(p0 - p1) < beta)) continue;
            if (bs[0] == 2) {    // ManyCam decides the strong filter from the first segment for all 8 lines (sic)
                bool small = d0 < (alpha >> 2) + 2;
                pl[p] = (byte)((Math.Abs(q0 - q2) < beta && small) ? (q1 + q0 * 2 + 2 + p0) >> 2 : (q1 * 2 + q0 + 2 + p0) >> 2);
                pl[p - across] = (byte)((Math.Abs(p0 - p2) < beta && small) ? (p1 + p0 * 2 + 2 + q0) >> 2 : (p1 * 2 + p0 + 2 + q0) >> 2);
            } else {
                int d = Clip3(-cl, cl, ((q0 - p0) * 3 + 4 + (p1 - q1)) >> 3);
                pl[p - across] = (byte)Clip255(p0 + d); pl[p] = (byte)Clip255(q0 - d);
            }
        }
    }
}
}
