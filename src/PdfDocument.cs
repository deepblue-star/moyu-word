using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MoyuWord
{
    public sealed class PdfDocument : IDisposable
    {
        private const int MaximumFileBytes = 128 * 1024 * 1024;
        private const int MaximumDimension = 4096;
        private const int MaximumPixels = 8000000;
        // PDFium is not thread safe. This lock covers all documents, not only this instance.
        private static readonly object NativeLock = new object();
        private static bool initialized;
        private static IntPtr nativeModule;
        private IntPtr document;
        private IntPtr fileMemory;
        private int pageCount;
        private bool disposed;

        public PdfDocument(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) throw new ArgumentException("请选择本地 PDF 文件。", "path");
            try
            {
                int length;
                // Read with .NET's Unicode path support. PDFium owns neither this file nor its buffer.
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (stream.Length == 0) throw new InvalidDataException("PDF 文件为空。");
                    if (stream.Length > MaximumFileBytes) throw new InvalidDataException("PDF 超过 128 MB，请先拆分文件后打开。");
                    length = checked((int)stream.Length);
                    fileMemory = Marshal.AllocHGlobal(length);
                    byte[] buffer = new byte[Math.Min(length, 65536)];
                    int offset = 0;
                    while (offset < length)
                    {
                        int read = stream.Read(buffer, 0, Math.Min(buffer.Length, length - offset));
                        if (read == 0) throw new EndOfStreamException("读取 PDF 时文件意外结束。");
                        Marshal.Copy(buffer, 0, IntPtr.Add(fileMemory, offset), read);
                        offset += read;
                    }
                }
                lock (NativeLock)
                {
                    EnsureInitialized();
                    document = Native.FPDF_LoadMemDocument64(fileMemory, new UIntPtr((uint)length), IntPtr.Zero);
                    if (document == IntPtr.Zero) throw PdfError("无法打开 PDF");
                    pageCount = Native.FPDF_GetPageCount(document);
                    if (pageCount <= 0) throw new InvalidDataException("PDF 中没有可读取的页面。");
                }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public int PageCount
        {
            get { lock (NativeLock) { ThrowIfDisposed(); return pageCount; } }
        }

        public double GetPageWidth(int index) { return GetPageDimension(index, true); }
        public double GetPageHeight(int index) { return GetPageDimension(index, false); }

        private double GetPageDimension(int index, bool width)
        {
            lock (NativeLock)
            {
                ValidatePage(index);
                IntPtr page = LoadPage(index);
                try { return ReadDimension(page, width); }
                finally { Native.FPDF_ClosePage(page); }
            }
        }

        /// <summary>Scale is pixels per PDF point; 1 is 72 dpi. Large output is proportionally capped.</summary>
        public BitmapSource Render(int pageIndex, double scale)
        {
            lock (NativeLock)
            {
                ValidatePage(pageIndex);
                if (Double.IsNaN(scale) || Double.IsInfinity(scale) || scale <= 0)
                    throw new ArgumentOutOfRangeException("scale", "缩放比例必须是正数。");
                IntPtr page = LoadPage(pageIndex);
                IntPtr bitmap = IntPtr.Zero;
                try
                {
                    double width = ReadDimension(page, true);
                    double height = ReadDimension(page, false);
                    double maxScale = Math.Min(MaximumDimension / width, MaximumDimension / height);
                    // Divide before multiplying to avoid overflow for unusual PDF media boxes.
                    maxScale = Math.Min(maxScale, Math.Sqrt(MaximumPixels / width / height));
                    double actualScale = Math.Min(scale, maxScale);
                    int pixelWidth = Math.Max(1, (int)Math.Floor(width * actualScale));
                    int pixelHeight = Math.Max(1, (int)Math.Floor(height * actualScale));
                    // A one-pixel minimum can increase the area for pathological aspect ratios.
                    pixelWidth = Math.Min(pixelWidth, MaximumPixels / pixelHeight);
                    bitmap = Native.FPDFBitmap_Create(pixelWidth, pixelHeight, 1);
                    if (bitmap == IntPtr.Zero) throw new OutOfMemoryException("没有足够内存来渲染此 PDF 页面。");
                    if (Native.FPDFBitmap_FillRect(bitmap, 0, 0, pixelWidth, pixelHeight, 0) == 0)
                        throw new InvalidDataException("无法初始化 PDF 页面图像。");
                    // Transparent paper allows the application's independently adjustable backdrop.
                    // Static annotations are rendered; no form environment, JavaScript or actions run.
                    Native.FPDF_RenderPageBitmap(bitmap, page, 0, 0, pixelWidth, pixelHeight, 0, 1);
                    int stride = Native.FPDFBitmap_GetStride(bitmap);
                    IntPtr buffer = Native.FPDFBitmap_GetBuffer(bitmap);
                    if (buffer == IntPtr.Zero || stride < pixelWidth * 4)
                        throw new InvalidDataException("PDF 渲染器返回了无效图像。");
                    BitmapSource result = BitmapSource.Create(pixelWidth, pixelHeight, 96, 96,
                        PixelFormats.Bgra32, null, buffer, checked(stride * pixelHeight), stride);
                    result.Freeze();
                    return result;
                }
                finally
                {
                    if (bitmap != IntPtr.Zero) Native.FPDFBitmap_Destroy(bitmap);
                    Native.FPDF_ClosePage(page);
                }
            }
        }

        private static double ReadDimension(IntPtr page, bool width)
        {
            double result = width ? Native.FPDF_GetPageWidth(page) : Native.FPDF_GetPageHeight(page);
            if (Double.IsNaN(result) || Double.IsInfinity(result) || result <= 0 || result > 10000000)
                throw new InvalidDataException("PDF 页面尺寸无效或过大。");
            return result;
        }

        private IntPtr LoadPage(int index)
        {
            IntPtr page = Native.FPDF_LoadPage(document, index);
            if (page == IntPtr.Zero) throw PdfError("无法读取 PDF 页面");
            return page;
        }

        private void ValidatePage(int index)
        {
            ThrowIfDisposed();
            if (index < 0 || index >= pageCount) throw new ArgumentOutOfRangeException("index");
        }

        private void ThrowIfDisposed()
        {
            if (disposed) throw new ObjectDisposedException("PdfDocument");
        }

        private static InvalidDataException PdfError(string message)
        {
            uint code = Native.FPDF_GetLastError();
            string reason = code == 4 ? "文件需要密码；当前版本暂不支持加密 PDF。" :
                code == 3 ? "文件已损坏或不是有效的 PDF。" : "文件无法解析（错误 " + code + "）。";
            return new InvalidDataException(message + "：" + reason);
        }

        private static void EnsureInitialized()
        {
            if (initialized) return;
            if (IntPtr.Size != 4) throw new PlatformNotSupportedException("请使用随程序提供的 32 位版本。");
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "native", "pdfium.dll");
            if (!File.Exists(path)) throw new DllNotFoundException("缺少 PDF 引擎，请重新安装程序：" + path);
            // Only the explicit application DLL directory and Windows system directory are searched.
            nativeModule = Native.LoadLibraryEx(path, IntPtr.Zero, 0x00000100 | 0x00000800);
            if (nativeModule == IntPtr.Zero)
                throw new InvalidOperationException("无法加载 PDF 引擎。", new Win32Exception(Marshal.GetLastWin32Error()));
            Native.FPDF_InitLibrary();
            initialized = true;
            // Keep the library initialized for the process lifetime; each document/page/bitmap is freed.
        }

        public void Dispose()
        {
            lock (NativeLock)
            {
                if (disposed) return;
                disposed = true;
                if (document != IntPtr.Zero) { Native.FPDF_CloseDocument(document); document = IntPtr.Zero; }
                if (fileMemory != IntPtr.Zero) { Marshal.FreeHGlobal(fileMemory); fileMemory = IntPtr.Zero; }
            }
            GC.SuppressFinalize(this);
        }

        ~PdfDocument() { Dispose(); }

        private static class Native
        {
            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            internal static extern IntPtr LoadLibraryEx(string fileName, IntPtr reserved, uint flags);
            [DllImport("pdfium.dll", CallingConvention = CallingConvention.StdCall)]
            internal static extern void FPDF_InitLibrary();
            [DllImport("pdfium.dll", CallingConvention = CallingConvention.StdCall)]
            internal static extern IntPtr FPDF_LoadMemDocument64(IntPtr data, UIntPtr size, IntPtr password);
            [DllImport("pdfium.dll", CallingConvention = CallingConvention.StdCall)]
            internal static extern uint FPDF_GetLastError();
            [DllImport("pdfium.dll", CallingConvention = CallingConvention.StdCall)]
            internal static extern int FPDF_GetPageCount(IntPtr document);
            [DllImport("pdfium.dll", CallingConvention = CallingConvention.StdCall)]
            internal static extern IntPtr FPDF_LoadPage(IntPtr document, int index);
            [DllImport("pdfium.dll", CallingConvention = CallingConvention.StdCall)]
            internal static extern double FPDF_GetPageWidth(IntPtr page);
            [DllImport("pdfium.dll", CallingConvention = CallingConvention.StdCall)]
            internal static extern double FPDF_GetPageHeight(IntPtr page);
            [DllImport("pdfium.dll", CallingConvention = CallingConvention.StdCall)]
            internal static extern void FPDF_ClosePage(IntPtr page);
            [DllImport("pdfium.dll", CallingConvention = CallingConvention.StdCall)]
            internal static extern void FPDF_CloseDocument(IntPtr document);
            [DllImport("pdfium.dll", CallingConvention = CallingConvention.StdCall)]
            internal static extern IntPtr FPDFBitmap_Create(int width, int height, int alpha);
            [DllImport("pdfium.dll", CallingConvention = CallingConvention.StdCall)]
            internal static extern int FPDFBitmap_FillRect(IntPtr bitmap, int left, int top, int width, int height, uint color);
            [DllImport("pdfium.dll", CallingConvention = CallingConvention.StdCall)]
            internal static extern void FPDF_RenderPageBitmap(IntPtr bitmap, IntPtr page, int x, int y, int width, int height, int rotate, int flags);
            [DllImport("pdfium.dll", CallingConvention = CallingConvention.StdCall)]
            internal static extern IntPtr FPDFBitmap_GetBuffer(IntPtr bitmap);
            [DllImport("pdfium.dll", CallingConvention = CallingConvention.StdCall)]
            internal static extern int FPDFBitmap_GetStride(IntPtr bitmap);
            [DllImport("pdfium.dll", CallingConvention = CallingConvention.StdCall)]
            internal static extern void FPDFBitmap_Destroy(IntPtr bitmap);
        }
    }
}
