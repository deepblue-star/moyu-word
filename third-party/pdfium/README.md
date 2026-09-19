# PDFium native dependency

The application bundles the unmodified `native/pdfium.dll` from Benoit Blanchon's
PDFium binary distribution. It is loaded locally; neither the application nor the
normal build downloads a runtime, and opening a PDF does not access the network.

- Binary project: https://github.com/bblanchon/pdfium-binaries
- Pinned release: https://github.com/bblanchon/pdfium-binaries/releases/tag/chromium/8057
- Archive: https://github.com/bblanchon/pdfium-binaries/releases/download/chromium/8057/pdfium-win-x86.tgz
- PDFium version: **155.0.8057.0**, Windows **x86** (PE machine `0x014c`).
- Retrieved: 2026-09-19.
- Archive size: 3,686,985 bytes.
- Archive SHA-256: `242AA2959BE60FB5224011130E98FAB71E83330F1BAD4F728F5D78495624251F`.
- DLL size: 6,836,736 bytes.
- DLL SHA-256: `5D8025AE0F7E501DE11842FEFC0F7B723E50480DB916DBFC5F59BC062F4F7E0F`.
- Upstream source: https://pdfium.googlesource.com/pdfium/

`args.gn` and `VERSION` are copied verbatim from that archive. The archive's build
configuration disables **V8** and **XFA**. The application's wrapper does not create
a form environment or invoke document actions, JavaScript, links or attachments.
It only reads local bytes, loads pages, and renders static page contents and
annotations. Password-protected PDFs are reported as unsupported.

The DLL's PE import table was inspected: its only direct dependencies are
`KERNEL32.dll`, `ADVAPI32.dll`, `GDI32.dll` and `USER32.dll`, all supplied by Windows.
It does not require a separately installed Visual C++ runtime. The C API uses the
Windows x86 `stdcall` calling convention.

## Re-acquiring the exact dependency

Run `powershell -ExecutionPolicy Bypass -File scripts/fetch-pdfium.ps1` from the
repository. This development-only script downloads the pinned archive, validates
both archive and DLL SHA-256 values and the x86 PE machine, then copies the DLL
and all notices. For an offline cached archive, pass `-ArchivePath <archive.tgz>`.
The normal release build uses the already bundled DLL and needs no network.

## Licenses and redistribution

`LICENSE` is the binary packaging project's MIT license. **It is not the only
license that applies.** The complete archive-provided `licenses/` directory is
included alongside it and must be distributed with the DLL:

- `pdfium.txt`: PDFium BSD-style terms and included notices.
- `abseil.txt`, `agg23.txt`, `fast_float.txt`, `freetype.txt`, `icu.txt`,
  `lcms.txt`, `libjpeg_turbo.ijg`, `libjpeg_turbo.md`, `libopenjpeg.txt`,
  `libpng.txt`, `llvm-libc.txt`, `simdutf.txt`, and `zlib.txt`: dependency notices.

Keep this directory in both portable and installed distributions. The repository's
own license does not replace these upstream terms.

## Wrapper limits and validation

The x86 wrapper retains at most 128 MiB of source file bytes. A render is capped
proportionally at 4,096 pixels per dimension and 8,000,000 pixels total. Blank
paper is transparent so the application can control its backdrop; explicit PDF
graphics, including a painted white rectangle or scanned page, retain their color.

`tests/fixtures/two-pages.pdf` is an original, minimal PDF 1.4 test document with
selectable Helvetica text, a 300 x 400 point portrait page, a 400 x 300 point
landscape page, and distinct blue/red shapes. `tests/PdfTests.cs` checks real
native loading, text/shape pixels, transparency, zoom, Unicode paths, invalid
files/indices/scales, source-file release, disposal, and repeated rendering.
