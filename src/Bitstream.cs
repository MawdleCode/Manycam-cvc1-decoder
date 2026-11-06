using System;
using System.Collections.Generic;

namespace Cvc1 {

/// <summary>MSB-first bit reader over a byte range.</summary>
public sealed class BitReader {
    readonly byte[] d; readonly int end; public long Pos;
    public BitReader(byte[] data, int startByte, int endByte) { d = data; Pos = (long)startByte * 8; end = Math.Min(endByte, data.Length); }
    public int Bit() {
        int i = (int)(Pos >> 3);
        int v = i < end ? (d[i] >> (7 - (int)(Pos & 7))) & 1 : 0;
        Pos++; return v;
    }
    public uint Read(int n) { uint v = 0; for (int i = 0; i < n; i++) v = (v << 1) | (uint)Bit(); return v; }
    /// <summary>Order-0 Exp-Golomb, unsigned.</summary>
    public int Ue() {
        int zeros = 0;
        while (Bit() == 0) { if (++zeros > 31) throw new InvalidOperationException("corrupt ue(v)"); }
        return (int)(((1u << zeros) | Read(zeros)) - 1);
    }
    /// <summary>Order-0 Exp-Golomb, signed (1 -> +1, 2 -> -1, ...).</summary>
    public int Se() { int u = Ue(); return (u & 1) == 1 ? (u + 1) >> 1 : -(u >> 1); }
    public long BitsLeft { get { return (long)end * 8 - Pos; } }
}

/// <summary>Variable-length code table: (length, code) -> symbol index.</summary>
public sealed class Vlc {
    readonly Dictionary<int, int> map = new Dictionary<int, int>(); int maxLen;
    public Vlc(short[] table, int offset, int entries) {
        for (int idx = 0; idx < entries; idx++) {
            int code = table[offset + idx * 2], len = table[offset + idx * 2 + 1];
            if (len <= 0) continue;
            int key = (len << 20) | code;
            if (!map.ContainsKey(key)) map[key] = idx;
            if (len > maxLen) maxLen = len;
        }
    }
    public int Decode(BitReader br) {
        int code = 0;
        for (int len = 1; len <= maxLen; len++) {
            code = (code << 1) | br.Bit();
            int idx; if (map.TryGetValue((len << 20) | code, out idx)) return idx;
        }
        throw new InvalidOperationException("invalid VLC code");
    }
}

/// <summary>Run/level symbol maps built from the embedded tables.</summary>
public sealed class CoefTables {
    public readonly Vlc[] IntraVlc = new Vlc[7], InterVlc = new Vlc[4], ChromaVlc = new Vlc[4];
    public readonly int[,] IntraL = new int[7, 64], IntraRun = new int[7, 64];
    public readonly int[,] InterL = new int[4, 64], InterRun = new int[4, 64];
    public readonly int[,] ChromaL = new int[4, 64], ChromaRun = new int[4, 64];

    public CoefTables() {
        for (int ctx = 0; ctx < 7; ctx++) {
            IntraVlc[ctx] = new Vlc(Tbl.IntraLumaVlc, ctx * 94, 47);
            for (int i = 0; i < 64; i++) { IntraL[ctx, i] = -1; IntraRun[ctx, i] = -1; }
            for (int L = 1; L <= Tbl.IntraLumaMaxLevel[ctx]; L++) {
                int b = Tbl.IntraLumaBase[ctx * 34 + L], mr = Tbl.IntraLumaMaxRun[ctx * 34 + L];
                for (int r = 0; r < mr; r++) { IntraL[ctx, b + r] = L; IntraRun[ctx, b + r] = r; }
            }
        }
        for (int ctx = 0; ctx < 4; ctx++) {
            InterVlc[ctx] = new Vlc(Tbl.InterLumaVlc, ctx * 82, 41);
            for (int i = 0; i < 64; i++) { InterL[ctx, i] = -1; InterRun[ctx, i] = -1; }
            for (int L = 1; L <= Tbl.InterLumaMaxLevel[ctx]; L++) {
                int b = Tbl.InterLumaBase[ctx * 13 + L], mr = Tbl.InterLumaMaxRun[ctx * 13 + L];
                for (int r = 0; r < mr; r++) { InterL[ctx, b + r] = L; InterRun[ctx, b + r] = r; }
            }
            ChromaVlc[ctx] = new Vlc(Tbl.ChromaVlc, ctx * 62, 31);
            for (int i = 0; i < 64; i++) { ChromaL[ctx, i] = 0; ChromaRun[ctx, i] = -1; }
            for (int L = 1; L <= Tbl.ChromaMaxLevel[ctx]; L++) for (int s = 0; s < 2; s++) {
                int b = Tbl.ChromaBase[ctx * 15 + 2 * L + s], mr = Tbl.ChromaMaxRun[ctx * 16 + 2 * L + s];
                for (int r = 0; r < mr; r++) { ChromaL[ctx, b + r] = s == 1 ? L : -L; ChromaRun[ctx, b + r] = r; }
            }
        }
    }
}
}
