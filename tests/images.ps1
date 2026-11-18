# Image helpers for the mock HMD's dumps. Dot-source this file after lab.ps1.
# The dumps are 32-bit top-down BMPs. System.Drawing ignores their alpha channel, so these read the bytes directly.

Add-Type -TypeDefinition @"
using System;
using System.IO;
public static class DevrBmp {
    public class Img { public int W, H; public byte[] Px; }  // BGRA, row 0 at the top
    public static Img Load(string path) {
        var b = File.ReadAllBytes(path);
        int off = BitConverter.ToInt32(b, 10), w = BitConverter.ToInt32(b, 18), h = BitConverter.ToInt32(b, 22);
        bool topDown = h < 0; h = Math.Abs(h);
        var img = new Img { W = w, H = h, Px = new byte[w * h * 4] };
        for (int y = 0; y < h; y++) Buffer.BlockCopy(b, off + (topDown ? y : h - 1 - y) * w * 4, img.Px, y * w * 4, w * 4);
        return img;
    }
    // Fine detail in a band of rows: mean absolute difference between neighbouring pixels, divided by the
    // band's mean brightness so darker or brighter frames compare fairly. Higher is sharper.
    public static double Sharpness(Img a, int y0, int y1, int x0, int x1) {
        double d = 0, m = 0; long n = 0;
        for (int y = y0; y < y1 - 1; y++) for (int x = x0; x < x1 - 1; x++) {
            int i = (y * a.W + x) * 4, r = i + 4, b = i + a.W * 4;
            for (int c = 0; c < 3; c++) { d += Math.Abs(a.Px[i + c] - a.Px[r + c]) + Math.Abs(a.Px[i + c] - a.Px[b + c]); m += a.Px[i + c]; }
            n++;
        }
        return m > 0 ? d / m : 0;
    }
    // Finds the scale (about the image centre) that best maps image a onto image b: b(c + (p - c) * s) ~ a(p).
    // Above 1 means b shows the scene magnified (a narrower field of view). Searched from 0.85 to 1.25.
    public static double ScaleMatch(Img a, Img b, out double bestErr) {
        double best = 1; bestErr = double.MaxValue;
        double cx = a.W / 2.0, cy = a.H / 2.0;
        for (double s = 0.85; s <= 1.2501; s += 0.005) {
            double err = 0; long n = 0;
            for (int y = a.H / 6; y < a.H * 5 / 6; y += 6) for (int x = a.W / 6; x < a.W * 5 / 6; x += 6) {
                int bx = (int)(cx + (x - cx) * s), by = (int)(cy + (y - cy) * s);
                if (bx < 0 || by < 0 || bx >= b.W || by >= b.H) continue;
                int i = (y * a.W + x) * 4, j = (by * b.W + bx) * 4;
                for (int c = 0; c < 3; c++) err += Math.Abs(a.Px[i + c] - b.Px[j + c]);
                n++;
            }
            err /= n;
            if (err < bestErr) { bestErr = err; best = s; }
        }
        return best;
    }
    // Brightness (the largest of R, G, B, as Shadow Depth uses) at a percentile from 0 to 1, as 0 to 255.
    public static int Pct(Img a, double p) {
        var h = new long[256]; long n = 0;
        for (int i = 0; i < a.Px.Length; i += 16) { int v = Math.Max(a.Px[i], Math.Max(a.Px[i + 1], a.Px[i + 2])); h[v]++; n++; }
        long want = (long)(n * p), c = 0;
        for (int v = 0; v < 256; v++) { c += h[v]; if (c >= want) return v; }
        return 255;
    }
    // Where two same-size images differ: count, bounding box and largest difference.
    public static string DiffWhere(Img a, Img b) {
        int n = 0, x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1, mx = 0;
        for (int y = 0; y < a.H; y++) for (int x = 0; x < a.W; x++) {
            int i = (y * a.W + x) * 4, d = 0;
            for (int c = 0; c < 3; c++) d = Math.Max(d, Math.Abs(a.Px[i + c] - b.Px[i + c]));
            if (d == 0) continue;
            n++; mx = Math.Max(mx, d); x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x); y1 = Math.Max(y1, y);
        }
        return n == 0 ? "identical" : n + " pixels differ (max " + mx + ") within x " + x0 + "-" + x1 + ", y " + y0 + "-" + y1;
    }
    // Share of pixels with alpha above the threshold, from 0 to 1.
    public static double Coverage(Img a, int threshold) {
        long n = 0; for (int i = 3; i < a.Px.Length; i += 4) if (a.Px[i] > threshold) n++;
        return (double)n / (a.W * a.H);
    }
    // Mean absolute RGB difference between two images of the same size, 0 to 255, on a coarse grid.
    public static double Diff(Img a, Img b, int step) {
        double sum = 0; long n = 0;
        for (int y = step / 2; y < a.H; y += step) for (int x = step / 2; x < a.W; x += step) {
            int i = (y * a.W + x) * 4;
            for (int c = 0; c < 3; c++) { sum += Math.Abs(a.Px[i + c] - b.Px[i + c]); n++; }
        }
        return sum / n;
    }
    // Mean brightness, 0 to 255.
    public static double Mean(Img a, int step) {
        double sum = 0; long n = 0;
        for (int y = step / 2; y < a.H; y += step) for (int x = step / 2; x < a.W; x += step) {
            int i = (y * a.W + x) * 4; sum += (a.Px[i] + a.Px[i + 1] + a.Px[i + 2]) / 3.0; n++;
        }
        return sum / n;
    }
    // Finds the horizontal shift of the right image that best matches the left, in pixels, in a band around
    // the given row. Positive means content sits further left in the right eye, as for anything in front of
    // the screen plane at infinity.
    public static int Disparity(Img l, Img r, int row, int band, int maxShift) {
        int best = 0; double bestErr = double.MaxValue;
        for (int s = -maxShift; s <= maxShift; s++) {
            double err = 0; long n = 0;
            for (int y = row - band; y < row + band; y += 2) for (int x = maxShift; x < l.W - maxShift; x += 3) {
                int i = (y * l.W + x) * 4, j = (y * l.W + x - s) * 4;
                for (int c = 0; c < 3; c++) err += Math.Abs(l.Px[i + c] - r.Px[j + c]);
                n++;
            }
            err /= n;
            if (err < bestErr) { bestErr = err; best = s; }
        }
        return best;
    }
    // Finds the vertical shift of image b that best matches image a, in pixels, over a central window. Positive
    // means the content sits lower in b (row numbers grow downwards).
    public static int VShift(Img a, Img b, int maxShift) {
        int best = 0; double bestErr = double.MaxValue;
        for (int s = -maxShift; s <= maxShift; s++) {
            double err = 0; long n = 0;
            for (int y = a.H / 4; y < a.H * 3 / 4; y += 3) for (int x = a.W / 4; x < a.W * 3 / 4; x += 3) {
                int i = (y * a.W + x) * 4, j = ((y + s) * a.W + x) * 4;
                for (int c = 0; c < 3; c++) err += Math.Abs(a.Px[i + c] - b.Px[j + c]);
                n++;
            }
            err /= n;
            if (err < bestErr) { bestErr = err; best = s; }
        }
        return best;
    }
    // Stereo disparity measured in view directions: for each candidate shift (in left-image pixels), compares the
    // left image with the right image at the same direction moved by that shift. Positive means content appears
    // further left in the right eye, as for anything nearer than infinity. Tangents are left, right, up, down.
    public static int DirDisparity(Img l, Img r, double[] tl, double[] tr, int row, int band, int maxShift) {
        int best = 0; double bestErr = double.MaxValue;
        double scaleL = (tl[1] - tl[0]) / l.W, scaleR = (tr[1] - tr[0]) / r.W;
        for (int s = -maxShift; s <= maxShift; s++) {
            double err = 0; long n = 0;
            for (int y = row - band; y < row + band; y += 2) for (int x = 0; x < l.W; x += 3) {
                double t = tl[0] + (x + 0.5) * scaleL - s * scaleL;
                int xr = (int)((t - tr[0]) / scaleR);
                if (xr < 0 || xr >= r.W) continue;
                int i = (y * l.W + x) * 4, j = (y * r.W + xr) * 4;
                for (int c = 0; c < 3; c++) err += Math.Abs(l.Px[i + c] - r.Px[j + c]);
                n++;
            }
            if (n == 0) continue;
            err /= n;
            if (err < bestErr) { bestErr = err; best = s; }
        }
        return best;
    }
    // Compares an eye rendered with the headset's own (asymmetric) view against the same eye rendered with a wider
    // symmetric view. Each pixel of the asymmetric image is looked up at the same view direction in the symmetric
    // one. Tangents are left, right, up, down. Returns the mean absolute RGB difference, 0 to 255.
    public static double ProjectionDiff(Img asym, Img sym, double[] a, double[] s, int step) {
        double sum = 0; long n = 0;
        for (int y = step / 2; y < asym.H; y += step) for (int x = step / 2; x < asym.W; x += step) {
            double tx = a[0] + (x + 0.5) / asym.W * (a[1] - a[0]);
            double ty = a[2] - (y + 0.5) / asym.H * (a[2] - a[3]);
            int sx = (int)((tx - s[0]) / (s[1] - s[0]) * sym.W), sy = (int)((s[2] - ty) / (s[2] - s[3]) * sym.H);
            if (sx < 0 || sy < 0 || sx >= sym.W || sy >= sym.H) continue;
            int i = (y * asym.W + x) * 4, j = (sy * sym.W + sx) * 4;
            for (int c = 0; c < 3; c++) { sum += Math.Abs(asym.Px[i + c] - sym.Px[j + c]); n++; }
        }
        return sum / n;
    }
    // The same comparison with the symmetric image deliberately misplaced by a few pixels, as a yardstick for how
    // large a real misalignment would make the difference.
    public static double ProjectionDiffShifted(Img asym, Img sym, double[] a, double[] s, int step, int shift) {
        double sum = 0; long n = 0;
        for (int y = step / 2; y < asym.H; y += step) for (int x = step / 2; x < asym.W; x += step) {
            double tx = a[0] + (x + 0.5) / asym.W * (a[1] - a[0]);
            double ty = a[2] - (y + 0.5) / asym.H * (a[2] - a[3]);
            int sx = (int)((tx - s[0]) / (s[1] - s[0]) * sym.W) + shift, sy = (int)((s[2] - ty) / (s[2] - s[3]) * sym.H);
            if (sx < 0 || sy < 0 || sx >= sym.W || sy >= sym.H) continue;
            int i = (y * asym.W + x) * 4, j = (sy * sym.W + sx) * 4;
            for (int c = 0; c < 3; c++) { sum += Math.Abs(asym.Px[i + c] - sym.Px[j + c]); n++; }
        }
        return sum / n;
    }
}
"@

# Saves an image dump as a PNG for looking at. System.Drawing ignores the BMP's alpha, so a UI dump shows its
# colours over black.
function Save-Flat([string]$Bmp, [string]$Png, [int]$Width = 800) {
    Add-Type -AssemblyName System.Drawing
    $i = [Drawing.Image]::FromFile($Bmp)
    $h = [int]($Width * $i.Height / $i.Width)
    $b = New-Object Drawing.Bitmap $Width, $h
    $g = [Drawing.Graphics]::FromImage($b)
    $g.DrawImage($i, 0, 0, $Width, $h)
    $b.Save($Png, [Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $b.Dispose(); $i.Dispose()
}
