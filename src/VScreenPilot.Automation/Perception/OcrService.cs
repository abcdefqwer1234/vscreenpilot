using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Security.Cryptography;
using VScreenPilot.Infrastructure;

namespace VScreenPilot.Perception;

internal sealed record ScreenTextLine(
    string Text, int Left, int Top, int Width, int Height);

/// <summary>
/// Windows 自带 OCR（Windows.Media.Ocr）。用于画布型应用（PowerPoint/图表/游戏）。
/// 不替代 UIA：结果带区域，且必须允许失败。
/// 中文环境对英文短横线识别很差，因此按查询脚本选择 en-US/中文引擎。
/// </summary>
internal sealed class OcrService
{
    private readonly object _sync = new();
    private readonly Dictionary<string, OcrEngine> _engines = new(StringComparer.OrdinalIgnoreCase);

    public (string Engine, string Language) Describe(bool preferLatin = false)
    {
        OcrEngine? engine = EnsureEngine(preferLatin, !preferLatin, out string language);
        return engine is null
            ? ("windows-ocr", string.Empty)
            : ("windows-ocr", language);
    }

    public List<ScreenTextLine> Read(
        byte[] bgra, int width, int height,
        out string engine, out string language, string textHint,
        CancellationToken cancellationToken = default)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        bool preferLatin = IsAsciiOnly(textHint);
        bool preferCjk = !preferLatin && !string.IsNullOrWhiteSpace(textHint);
        OcrEngine? ocr = EnsureEngine(preferLatin, preferCjk, out language);

        if (ocr is null)
        {
            throw new NotSupportedException(
                "当前系统没有可用的 OCR 识别语言包（设置 → 时间和语言 → 语言 → 添加 OCR）");
        }

        engine = "windows-ocr";

        // GDI 输出是 BGRA 顶朝下；SoftwareBitmap 需要 IBuffer，Alpha 已固定为 255。
        var buffer = CryptographicBuffer.CreateFromByteArray(bgra);
        using SoftwareBitmap bitmap = SoftwareBitmap.CreateCopyFromBuffer(
            buffer, BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Premultiplied);

        OcrResult result = ocr.RecognizeAsync(bitmap)
            .AsTask(cancellationToken)
            .GetAwaiter()
            .GetResult();
        var lines = new List<ScreenTextLine>();

        foreach (var line in result.Lines)
        {
            double left = double.MaxValue, top = double.MaxValue;
            double right = double.MinValue, bottom = double.MinValue;
            var text = new System.Text.StringBuilder();

            foreach (OcrWord word in line.Words)
            {
                if (text.Length > 0)
                {
                    text.Append(' ');
                }

                text.Append(word.Text);
                left = Math.Min(left, word.BoundingRect.Left);
                top = Math.Min(top, word.BoundingRect.Top);
                right = Math.Max(right, word.BoundingRect.Right);
                bottom = Math.Max(bottom, word.BoundingRect.Bottom);
            }

            if (text.Length == 0 || left == double.MaxValue)
            {
                continue;
            }

            lines.Add(new ScreenTextLine(
                text.ToString(),
                (int)Math.Round(left),
                (int)Math.Round(top),
                Math.Max(1, (int)Math.Round(right - left)),
                Math.Max(1, (int)Math.Round(bottom - top))));
        }

        lines.Sort((a, b) => a.Top == b.Top
            ? a.Left.CompareTo(b.Left)
            : a.Top.CompareTo(b.Top));

        return lines;
    }

    private OcrEngine? EnsureEngine(bool preferLatin, bool preferCjk, out string language)
    {
        lock (_sync)
        {
            if (preferLatin && TryGet("en-US", preferLatin: true, out OcrEngine? latin, out language))
            {
                return latin;
            }

            if (preferCjk && TryGet("zh-Hans", preferLatin: false, out OcrEngine? cjk, out language))
            {
                return cjk;
            }

            if (TryGet(null, preferLatin: false, out OcrEngine? primary, out language))
            {
                return primary;
            }

            // 用户配置语言不可用时，依次尝试常见语言。
            foreach (string tag in new[] { "zh-Hans", "en-US" })
            {
                if (TryGet(tag, preferLatin: false, out OcrEngine? engine, out language))
                {
                    return engine;
                }
            }

            return null;
        }
    }

    private bool TryGet(
        string? tag, bool preferLatin, out OcrEngine? engine, out string language)
    {        string key = tag ?? "user-profile";

        if (_engines.TryGetValue(key, out OcrEngine? cached))
        {
            engine = cached;
            language = cached.RecognizerLanguage?.LanguageTag ?? key;
            return true;
        }

        try
        {
            OcrEngine? created = tag is null
                ? OcrEngine.TryCreateFromUserProfileLanguages()
                : OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language(tag));

            if (created is null)
            {
                engine = null;
                language = string.Empty;
                return false;
            }

            _engines[key] = created;
            engine = created;
            language = created.RecognizerLanguage?.LanguageTag ?? key;
            return true;
        }
        catch
        {
            engine = null;
            language = string.Empty;
            return false;
        }
    }

    private static bool IsAsciiOnly(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        foreach (char ch in value)
        {
            if (ch > 127)
            {
                return false;
            }
        }

        return true;
    }
}
