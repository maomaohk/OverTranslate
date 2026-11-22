using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using NLog;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace OverTranslate.Services.Ocr;

internal sealed class WindowsOcrEngine : IOcrEngine
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    private const double DefaultConfidence = 0.9;

    private readonly SemaphoreSlim _gate = new(2, 2);
    private bool _disposed;

    public async Task<List<OcrTextBlock>> RecognizeAsync(
        Bitmap bitmap,
        string sourceLanguage,
        CancellationToken cancellationToken = default,
        bool verticalText = false)
    {
        if (!OcrLanguageRouter.IsSupported(sourceLanguage))
            throw new NotSupportedException(OcrLanguageRouter.GetUnsupportedLanguageMessage(sourceLanguage));

        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await RecognizeCoreAsync(bitmap, sourceLanguage, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<List<OcrTextBlock>?> TryRecognizeAsync(
        Bitmap bitmap,
        string sourceLanguage,
        int? maxDetectSize = null,
        CancellationToken cancellationToken = default,
        bool verticalText = false)
    {
        if (!OcrLanguageRouter.IsSupported(sourceLanguage))
            throw new NotSupportedException(OcrLanguageRouter.GetUnsupportedLanguageMessage(sourceLanguage));

        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_gate.Wait(0, cancellationToken))
            return null;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await RecognizeCoreAsync(bitmap, sourceLanguage, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<List<OcrTextBlock>> RecognizeCoreAsync(
        Bitmap bitmap, string sourceLanguage, CancellationToken cancellationToken)
    {
        var normalizedLanguage = OcrLanguageRouter.Normalize(sourceLanguage);
        var engine = CreateEngine(normalizedLanguage);
        if (engine is null)
        {
            Log.Warn("Windows OCR could not find any installed OCR language pack for {Lang}", normalizedLanguage);
            return [];
        }

        using var softwareBitmap = ConvertToSoftwareBitmap(bitmap);
        var result = await engine.RecognizeAsync(softwareBitmap).AsTask(cancellationToken).ConfigureAwait(false);
        if (result?.Lines is null || result.Lines.Count == 0)
            return [];

        var blocks = new List<OcrTextBlock>(result.Lines.Count);
        foreach (var line in result.Lines)
        {
            if (line.Words is null || line.Words.Count == 0)
                continue;

            var text = NormalizeLineText(line.Text);
            if (string.IsNullOrWhiteSpace(text))
                continue;

            double left = double.PositiveInfinity;
            double top = double.PositiveInfinity;
            double right = double.NegativeInfinity;
            double bottom = double.NegativeInfinity;

            foreach (var word in line.Words)
            {
                var r = word.BoundingRect;
                if (r.X < left) left = r.X;
                if (r.Y < top) top = r.Y;
                if (r.X + r.Width > right) right = r.X + r.Width;
                if (r.Y + r.Height > bottom) bottom = r.Y + r.Height;
            }

            if (double.IsInfinity(left) || right <= left || bottom <= top)
                continue;

            var bounds = new System.Windows.Rect(left, top, right - left, bottom - top);
            blocks.Add(new OcrTextBlock(text, bounds, Confidence: DefaultConfidence));
        }

        if (blocks.Count == 0)
            return blocks;

        blocks = blocks.OrderBy(b => b.Bounds.Y).ThenBy(b => b.Bounds.X).ToList();

        return OcrLanguageRouter.UsesAutomaticLayout(normalizedLanguage)
            ? OnnxOcrEngine.NormalizeAutomaticBlocks(blocks)
            : OnnxOcrEngine.NormalizeBlocks(blocks, OcrLanguageRouter.UsesCjkOnnx(normalizedLanguage));
    }

    private static OcrEngine? CreateEngine(string normalizedLanguage)
    {
        foreach (var tag in LanguageTagsFor(normalizedLanguage))
        {
            try
            {
                var language = new Language(tag);
                if (OcrEngine.IsLanguageSupported(language))
                    return OcrEngine.TryCreateFromLanguage(language);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Windows OCR language check failed for {Tag}", tag);
            }
        }

        return OcrEngine.TryCreateFromUserProfileLanguages();
    }

    private static IReadOnlyList<string> LanguageTagsFor(string normalizedLanguage) =>
        normalizedLanguage switch
        {
            "ZH" => ["zh-Hans-CN", "zh-Hans", "zh-CN"],
            "ZH-HANT" => ["zh-Hant-TW", "zh-Hant", "zh-TW", "zh-HK"],
            "JA" => ["ja-JP", "ja"],
            "KO" => ["ko-KR", "ko"],
            "EN" => ["en-US", "en-GB", "en"],
            "RU" => ["ru-RU", "ru"],
            "UK" => ["uk-UA", "uk"],
            _ => [],
        };

    private static string NormalizeLineText(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var trimmed = raw.Trim();
        if (!trimmed.Contains(' ')) return trimmed;

        var sb = new StringBuilder(trimmed.Length);
        for (int i = 0; i < trimmed.Length; i++)
        {
            var c = trimmed[i];
            if (c == ' ' && i > 0 && i + 1 < trimmed.Length && IsCjkOrWide(trimmed[i - 1]) && IsCjkOrWide(trimmed[i + 1]))
                continue;
            sb.Append(c);
        }

        return sb.ToString();
    }

    private static bool IsCjkOrWide(char c) =>
        c is (>= '\u3000' and <= '\u9FFF')
        or (>= '\uAC00' and <= '\uD7AF')
        or (>= '\uFF00' and <= '\uFFEF');

    private static SoftwareBitmap ConvertToSoftwareBitmap(Bitmap bitmap)
    {
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var rowBytes = bitmap.Width * 4;
            var buffer = new byte[rowBytes * bitmap.Height];
            for (int y = 0; y < bitmap.Height; y++)
                Marshal.Copy(data.Scan0 + y * data.Stride, buffer, y * rowBytes, rowBytes);

            var softwareBitmap = new SoftwareBitmap(
                BitmapPixelFormat.Bgra8, bitmap.Width, bitmap.Height, BitmapAlphaMode.Premultiplied);
            softwareBitmap.CopyFromBuffer(buffer.AsBuffer());
            return softwareBitmap;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _gate.Dispose();
    }
}
