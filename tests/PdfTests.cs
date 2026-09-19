using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MoyuWord;

internal static class PdfTests
{
    private static int passed;

    [STAThread]
    private static int Main(string[] args)
    {
        string fixture = Path.GetFullPath(args.Length > 0 ? args[0] : "tests/fixtures/two-pages.pdf");
        string temporary = Path.Combine(Path.GetTempPath(), "MoyuPdfTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            Check(IntPtr.Size == 4, "test process is x86");
            string unicodePath = Path.Combine(temporary, "中文 PDF 样本.pdf");
            File.Copy(fixture, unicodePath);
            using (PdfDocument pdf = new PdfDocument(unicodePath))
            {
                Check(pdf.PageCount == 2, "reads real multipage PDF via Unicode path");
                Check(pdf.GetPageWidth(0) == 300 && pdf.GetPageHeight(0) == 400, "portrait page points");
                Check(pdf.GetPageWidth(1) == 400 && pdf.GetPageHeight(1) == 300, "landscape page points");
                BitmapSource first = pdf.Render(0, 1);
                Check(first.PixelWidth == 300 && first.PixelHeight == 400 && first.IsFrozen, "72 dpi bitmap is correct and frozen");
                byte[] firstPixels = Pixels(first);
                Check(firstPixels[3] == 0, "blank paper is transparent");
                int colored = (100 * first.PixelWidth + 50) * 4;
                Check(firstPixels[colored] > 150 && firstPixels[colored + 2] < 80 && firstPixels[colored + 3] == 255, "first page blue rectangle rendered");
                Check(CountDarkPixels(firstPixels, first.PixelWidth, 25, 25, 290, 65) > 100, "first page actual text is rendered");
                BitmapSource second = pdf.Render(1, 2);
                Check(second.PixelWidth == 800 && second.PixelHeight == 600, "zoom doubles pixels");
                byte[] secondPixels = Pixels(second);
                colored = (220 * second.PixelWidth + 100) * 4;
                Check(secondPixels[colored + 2] > 150 && secondPixels[colored] < 80 && secondPixels[colored + 3] == 255, "second page red rectangle rendered");
                BitmapSource bounded = pdf.Render(0, double.MaxValue);
                Check(bounded.PixelWidth <= 4096 && bounded.PixelHeight <= 4096 && (long)bounded.PixelWidth * bounded.PixelHeight <= 8000000, "huge zoom is memory-bounded");
                Expect<ArgumentOutOfRangeException>(delegate { pdf.Render(-1, 1); }, "negative page rejected");
                Expect<ArgumentOutOfRangeException>(delegate { pdf.GetPageHeight(2); }, "page past end rejected");
                Expect<ArgumentOutOfRangeException>(delegate { pdf.Render(0, 0); }, "zero zoom rejected");
                Expect<ArgumentOutOfRangeException>(delegate { pdf.Render(0, double.NaN); }, "NaN zoom rejected");
                Expect<ArgumentOutOfRangeException>(delegate { pdf.Render(0, double.PositiveInfinity); }, "infinite zoom rejected");
                // The input is copied to owned memory, so the file is no longer locked.
                File.Delete(unicodePath);
                Check(pdf.Render(1, 0.5).PixelWidth == 200, "render survives source file removal");
                SavePng(first, Path.Combine(temporary, "page-one.png"));
                SavePng(second, Path.Combine(temporary, "page-two.png"));
            }
            PdfDocument disposed = new PdfDocument(fixture);
            disposed.Dispose();
            disposed.Dispose();
            Expect<ObjectDisposedException>(delegate { disposed.Render(0, 1); }, "render after dispose rejected");
            Expect<ObjectDisposedException>(delegate { int ignored = disposed.PageCount; }, "properties after dispose rejected");
            Expect<FileNotFoundException>(delegate { using (new PdfDocument(Path.Combine(temporary, "missing.pdf"))) { } }, "missing path rejected");
            Expect<ArgumentException>(delegate { using (new PdfDocument(" ")) { } }, "blank path rejected");
            string corrupt = Path.Combine(temporary, "corrupt.pdf");
            File.WriteAllText(corrupt, "This is not a PDF document.");
            Expect<InvalidDataException>(delegate { using (new PdfDocument(corrupt)) { } }, "corrupt PDF rejected");
            for (int i = 0; i < 12; i++)
                using (PdfDocument pdf = new PdfDocument(fixture)) { pdf.Render(i % 2, 0.5); }
            Check(true, "repeated open/render/dispose succeeds");
            Console.WriteLine("PASS: " + passed + " PDF checks. Render previews: " + temporary);
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("FAIL after " + passed + " PDF checks: " + error);
            return 1;
        }
    }

    private static byte[] Pixels(BitmapSource bitmap)
    {
        byte[] pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return pixels;
    }

    private static int CountDarkPixels(byte[] pixels, int width, int left, int top, int right, int bottom)
    {
        int count = 0;
        for (int y = top; y < bottom; y++)
            for (int x = left; x < right; x++)
            {
                int i = (y * width + x) * 4;
                if (pixels[i] < 60 && pixels[i + 1] < 60 && pixels[i + 2] < 60 && pixels[i + 3] > 150) count++;
            }
        return count;
    }

    private static void SavePng(BitmapSource bitmap, string path)
    {
        // Show the transparent page on paper for visual QA, as the application does with its backdrop.
        DrawingVisual preview = new DrawingVisual();
        using (DrawingContext drawing = preview.RenderOpen())
        {
            Rect bounds = new Rect(0, 0, bitmap.PixelWidth, bitmap.PixelHeight);
            drawing.DrawRectangle(Brushes.White, null, bounds);
            drawing.DrawImage(bitmap, bounds);
        }
        RenderTargetBitmap rendered = new RenderTargetBitmap(bitmap.PixelWidth, bitmap.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        rendered.Render(preview);
        PngBitmapEncoder encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rendered));
        using (FileStream stream = File.Create(path)) encoder.Save(stream);
    }

    private static void Expect<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { Check(true, message); return; }
        throw new Exception("Expected " + typeof(T).Name + ": " + message);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        passed++;
        Console.WriteLine("  OK " + message);
    }
}
