using System;
using System.IO;
using System.Text;

namespace Cvc1 {

/// <summary>
/// Reads CVC1 video frames from an AVI file in stream order. Works on normal files and on damaged ones:
/// ManyCam writes the AVI header last, so recordings that were never closed properly often have a zeroed
/// header and no index. When the structure is unreadable this reader scans for '00db'/'00dc' frame chunks.
/// </summary>
public sealed class AviReader : IDisposable {
    readonly FileStream fs; readonly long len; long pos;
    readonly byte[] hd = new byte[12];
    public double Fps = 15;          // from the AVI header when present
    public bool HeaderFound;
    public long Resyncs, SkippedBytes;

    public AviReader(string path) {
        fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        len = fs.Length;
        var head = new byte[Math.Min(len, 0x20000)];
        fs.Read(head, 0, head.Length);
        if (head.Length >= 12 && Ascii(head, 0) == "RIFF" && Ascii(head, 8) == "AVI ") {
            HeaderFound = true;
            int avih = Find(head, "avih");
            if (avih >= 0 && avih + 12 <= head.Length) {
                uint usPerFrame = BitConverter.ToUInt32(head, avih + 8);
                if (usPerFrame > 1000 && usPerFrame < 10000000) {
                    Fps = 1e6 / usPerFrame;
                    if (Math.Abs(Fps - Math.Round(Fps)) < 0.01) Fps = Math.Round(Fps);   // 66666 us -> 15, not 15.00015
                }
            }
        }
        pos = 0;
    }

    static string Ascii(byte[] b, int o) { return Encoding.ASCII.GetString(b, o, 4); }
    static int Find(byte[] b, string tag) { for (int i = 0; i + 4 <= b.Length; i++) if (b[i] == tag[0] && b[i + 1] == tag[1] && b[i + 2] == tag[2] && b[i + 3] == tag[3]) return i; return -1; }
    static bool IsFourCC(byte[] b, int o) { for (int i = 0; i < 4; i++) { byte c = b[o + i]; if (c < 0x20 || c > 0x7E) return false; } return true; }
    static bool IsVideoTag(byte[] b, int o) { return b[o] >= '0' && b[o] <= '9' && b[o + 1] >= '0' && b[o + 1] <= '9' && (b[o + 2] == 'd') && (b[o + 3] == 'b' || b[o + 3] == 'c'); }

    bool LooksLikeFrame(long at, uint size) {
        if (size < 8 || size > (16u << 20) || at + 8 + size > len) return false;
        var b = new byte[4]; fs.Seek(at + 8, SeekOrigin.Begin); if (fs.Read(b, 0, 4) < 4) return false;
        return b[0] == 0xFF && b[1] == 0xFF && b[2] == 0xFF && (b[3] == 0x08 || (b[3] >= 0x40 && b[3] < 0x80));
    }

    /// <summary>Returns the next compressed frame, or null at end of file.</summary>
    public byte[] Next() {
        while (pos + 8 <= len) {
            fs.Seek(pos, SeekOrigin.Begin);
            if (fs.Read(hd, 0, 12) < 8) return null;
            uint size = BitConverter.ToUInt32(hd, 4);
            if (IsFourCC(hd, 0)) {
                string tag = Ascii(hd, 0);
                if (tag == "RIFF" || (tag == "LIST" && size >= 4)) {
                    string type = Ascii(hd, 8);
                    if (tag == "RIFF" || type == "movi" || type == "rec ") { pos += 12; continue; }   // descend
                    if (pos + 8 + size <= len) { pos += 8 + size + (size & 1); continue; }           // skip hdrl etc.
                }
                if (IsVideoTag(hd, 0)) {
                    if (LooksLikeFrame(pos, size)) {
                        var f = new byte[size]; fs.Seek(pos + 8, SeekOrigin.Begin);
                        int n = 0; while (n < size) { int r = fs.Read(f, n, (int)size - n); if (r <= 0) break; n += r; }
                        pos += 8 + size + (size & 1);
                        return f;
                    }
                } else if (size < len && pos + 8 + size <= len && tag != "RIFF" && tag != "LIST") {
                    pos += 8 + size + (size & 1); continue;  // JUNK, idx1, ix00, indx, ...
                }
            }
            // Unreadable here (zeroed header, garbage, truncated chunk): scan forward for the next frame.
            long found = Resync(pos + 1);
            if (found < 0) return null;
            SkippedBytes += found - pos; Resyncs++; pos = found;
        }
        return null;
    }

    long Resync(long from) {
        var buf = new byte[1 << 20];
        long s = from;
        while (s + 8 <= len) {
            fs.Seek(s, SeekOrigin.Begin);
            int n = fs.Read(buf, 0, buf.Length); if (n < 8) break;
            for (int i = 0; i + 8 <= n; i++) {
                if (buf[i] == '0' && buf[i + 2] == 'd' && IsVideoTag(buf, i)) {
                    uint size = BitConverter.ToUInt32(buf, i + 4);
                    long at = s + i; long save = fs.Position;
                    bool ok = LooksLikeFrame(at, size);
                    fs.Seek(save, SeekOrigin.Begin);
                    if (ok) return at;
                }
            }
            s += n - 7;
        }
        return -1;
    }

    public void Dispose() { fs.Dispose(); }
}
}
