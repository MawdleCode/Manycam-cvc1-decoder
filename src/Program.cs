using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace Cvc1 {

static class Program {
    const string Version = "1.0.0";

    static int Usage() {
        Console.WriteLine("cvc1conv " + Version + " - convert ManyCam 'CVC1' AVI recordings to MP4");
        Console.WriteLine();
        Console.WriteLine("Usage: cvc1conv [options] <file.avi | folder> [more files...]");
        Console.WriteLine();
        Console.WriteLine("  Each input becomes <name>.mp4 next to it (needs ffmpeg).");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  -o <file>        output file (single input only). Use .y4m for raw video without ffmpeg");
        Console.WriteLine("  --y4m            write .y4m (uncompressed) instead of .mp4; no ffmpeg needed");
        Console.WriteLine("  --fps <n>        frame rate to use (default: from the AVI header, usually 15)");
        Console.WriteLine("  --crf <n>        x264 quality, lower is better (default 18)");
        Console.WriteLine("  --ffmpeg <path>  path to ffmpeg.exe (default: next to cvc1conv.exe, then PATH)");
        Console.WriteLine("  --overwrite      replace existing output files");
        Console.WriteLine("  --no-deblock     disable the loop filter (debugging)");
        Console.WriteLine("  --md5            just decode and print an MD5 of all frames (testing)");
        return 1;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern uint GetConsoleProcessList(uint[] list, uint count);

    static int Main(string[] args) {
        int rc = Run(args);
        // Started by double-click or drag-and-drop (we own the console window): keep it open so the result can be read.
        try { if (GetConsoleProcessList(new uint[4], 4) == 1) { Console.WriteLine(); Console.WriteLine("Press any key to close..."); Console.ReadKey(true); } } catch { }
        return rc;
    }

    static int Run(string[] args) {
        var inputs = new List<string>(); string outArg = null, ffArg = null; double fps = 0; int crf = 18;
        bool y4m = false, overwrite = false, md5 = false, deblock = true;
        for (int i = 0; i < args.Length; i++) {
            string a = args[i];
            switch (a.ToLowerInvariant()) {
                case "-o": case "--output": if (++i < args.Length) outArg = args[i]; break;
                case "--fps": if (++i < args.Length) fps = double.Parse(args[i], CultureInfo.InvariantCulture); break;
                case "--crf": if (++i < args.Length) crf = int.Parse(args[i], CultureInfo.InvariantCulture); break;
                case "--ffmpeg": if (++i < args.Length) ffArg = args[i]; break;
                case "--y4m": y4m = true; break;
                case "--overwrite": case "-y": overwrite = true; break;
                case "--md5": md5 = true; break;
                case "--no-deblock": deblock = false; break;
                case "-h": case "--help": case "/?": return Usage();
                default:
                    if (Directory.Exists(a)) foreach (var f in Directory.GetFiles(a, "*.avi")) inputs.Add(f);
                    else inputs.Add(a);
                    break;
            }
        }
        if (inputs.Count == 0) return Usage();
        if (outArg != null && inputs.Count > 1) { Console.Error.WriteLine("-o can only be used with a single input"); return 1; }

        string ffmpeg = null;
        if (!md5) {
            if (outArg != null && outArg.EndsWith(".y4m", StringComparison.OrdinalIgnoreCase)) y4m = true;
            if (!y4m) {
                ffmpeg = FindFfmpeg(ffArg);
                if (ffmpeg == null) {
                    Console.Error.WriteLine("ffmpeg was not found. Put ffmpeg.exe next to cvc1conv.exe, add it to PATH,");
                    Console.Error.WriteLine("pass --ffmpeg <path>, or use --y4m to write uncompressed video instead.");
                    Console.Error.WriteLine("Get ffmpeg from https://www.gyan.dev/ffmpeg/builds/ or run: winget install ffmpeg");
                    return 2;
                }
            }
        }

        int failures = 0;
        foreach (var input in inputs) {
            if (!File.Exists(input)) { Console.Error.WriteLine("not found: " + input); failures++; continue; }
            string output = outArg ?? Path.ChangeExtension(input, y4m ? ".y4m" : ".mp4");
            if (!md5 && File.Exists(output) && !overwrite) { Console.Error.WriteLine("skipping (exists, use --overwrite): " + output); continue; }
            try { failures += Convert(input, output, ffmpeg, fps, crf, y4m, md5, deblock) ? 0 : 1; }
            catch (Exception ex) { Console.Error.WriteLine("error: " + ex.Message); failures++; }
        }
        return failures == 0 ? 0 : 3;
    }

    static string FindFfmpeg(string explicitPath) {
        if (explicitPath != null) return File.Exists(explicitPath) ? explicitPath : null;
        string here = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe");
        if (File.Exists(here)) return here;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)) {
            try { string p = Path.Combine(dir.Trim('"'), "ffmpeg.exe"); if (File.Exists(p)) return p; } catch { }
        }
        return null;
    }

    static bool Convert(string input, string output, string ffmpeg, double fpsOverride, int crf, bool y4m, bool md5Only, bool deblock) {
        Console.WriteLine(Path.GetFileName(input) + (md5Only ? "" : " -> " + output));
        var sw = Stopwatch.StartNew();
        using (var avi = new AviReader(input)) {
            double fps = fpsOverride > 0 ? fpsOverride : avi.Fps;
            var dec = new Decoder { Deblocking = deblock };
            Stream sink = null; Process proc = null; NamedPipeServerStream pipe = null; var ffErr = new StringBuilder();
            MD5 hash = md5Only ? MD5.Create() : null;
            byte[] frameBuf = null; Picture held = null, last = null;
            int nIn = 0, nOut = 0, errors = 0, repeated = 0;
            string firstError = null;

            Action<Picture> emit = pic => {
                if (frameBuf == null) {
                    frameBuf = new byte[pic.Y.Length + pic.U.Length + pic.V.Length];
                    if (md5Only) { }
                    else if (y4m) {
                        sink = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
                        byte[] h = Encoding.ASCII.GetBytes(string.Format(CultureInfo.InvariantCulture, "YUV4MPEG2 W{0} H{1} F{2}:1000 Ip A1:1 C420jpeg\n", pic.W, pic.H, (int)Math.Round(fps * 1000)));
                        sink.Write(h, 0, h.Length);
                    } else {
                        string pipeName = "cvc1conv_" + Guid.NewGuid().ToString("N");
                        pipe = new NamedPipeServerStream(pipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.None, 1 << 20, 1 << 20);
                        var psi = new ProcessStartInfo(ffmpeg, string.Format(CultureInfo.InvariantCulture,
                            "-hide_banner -loglevel error -y -f rawvideo -pix_fmt yuv420p -s {0}x{1} -r {2} -i \\\\.\\pipe\\{3} -c:v libx264 -preset slow -crf {4} -pix_fmt yuv420p -movflags +faststart \"{5}\"",
                            pic.W, pic.H, fps, pipeName, crf, output));
                        psi.UseShellExecute = false; psi.RedirectStandardError = true; psi.CreateNoWindow = true;
                        proc = Process.Start(psi);
                        proc.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (ffErr) ffErr.AppendLine(e.Data); };
                        proc.BeginErrorReadLine();
                        pipe.WaitForConnection();
                        sink = pipe;
                    }
                }
                Buffer.BlockCopy(pic.Y, 0, frameBuf, 0, pic.Y.Length);
                Buffer.BlockCopy(pic.U, 0, frameBuf, pic.Y.Length, pic.U.Length);
                Buffer.BlockCopy(pic.V, 0, frameBuf, pic.Y.Length + pic.U.Length, pic.V.Length);
                if (hash != null) hash.TransformBlock(frameBuf, 0, frameBuf.Length, null, 0);
                else {
                    if (y4m) { byte[] fh = Encoding.ASCII.GetBytes("FRAME\n"); sink.Write(fh, 0, fh.Length); }
                    sink.Write(frameBuf, 0, frameBuf.Length);
                }
                last = pic; nOut++;
                if (nOut % 2000 == 0) Console.Write("\r  {0} frames...", nOut);
            };

            byte[] f;
            while ((f = avi.Next()) != null) {
                nIn++;
                Picture pic = null;
                try { pic = dec.DecodeFrame(f); }
                catch (Exception ex) { errors++; if (firstError == null) firstError = "frame " + (nIn - 1) + ": " + ex.Message; }
                if (pic == null) { Picture rep = last ?? held; if (rep != null) { emit(rep); repeated++; } continue; }
                if (pic.Type == 2) emit(pic);                       // B pictures are shown before the anchor that precedes them in the file
                else { if (held != null) emit(held); held = pic; }
            }
            if (held != null) emit(held);
            if (nOut % 2000 >= 0) Console.Write("\r");

            if (sink != null) { sink.Flush(); sink.Dispose(); }
            if (proc != null) { proc.WaitForExit(); if (proc.ExitCode != 0) Console.Error.WriteLine("ffmpeg failed: " + ffErr.ToString().Trim()); }
            sw.Stop();
            if (nIn == 0) { Console.Error.WriteLine("  no CVC1 frames found - is this a ManyCam CVC1 recording?"); return false; }
            Console.WriteLine("  {0} frames ({1:0.0} fps, {2}) in {3:0}s{4}{5}", nOut, fps, TimeSpan.FromSeconds(nOut / fps).ToString(@"h\:mm\:ss"),
                sw.Elapsed.TotalSeconds, avi.HeaderFound ? "" : ", AVI header was damaged (frames recovered by scanning)",
                errors > 0 ? ", " + errors + " undecodable frames repeated" : "");
            if (firstError != null) Console.WriteLine("  first error: " + firstError);
            if (hash != null) { hash.TransformFinalBlock(new byte[0], 0, 0); Console.WriteLine("  md5 " + BitConverter.ToString(hash.Hash).Replace("-", "").ToLowerInvariant()); }
            return proc == null || proc.ExitCode == 0;
        }
    }
}
}
