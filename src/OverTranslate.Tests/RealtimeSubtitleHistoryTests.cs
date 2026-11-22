using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using OverTranslate.Services;
using OverTranslate.Services.Realtime;
using OverTranslate.Views.Realtime;
using Xunit;

namespace OverTranslate.Tests;

public class RealtimeSubtitleHistoryTests
{
    private static TranslatedBlock Line(string text, double top = 220, double height = 36, RealtimeBlockMode? readAs = null) =>
        new("source:" + text, text, new Rect(40, top, 520, height)) { ReadAs = readAs };

    private static RealtimeBlockWindow CreateLoadedWindow(
        int height = 300,
        RealtimeBlockMode mode = RealtimeBlockMode.Subtitle,
        int subtitleHoldMs = 3000,
        int subtitleMaxHistory = 3)
    {
        var window = new RealtimeBlockWindow(
            0, new System.Drawing.Rectangle(0, 0, 800, height),
            _ => null, "EN", "ZH-HANT", "#FFFFFF", "#000000", 70,
            mode: mode,
            subtitleHoldMs: subtitleHoldMs,
            subtitleMaxHistory: subtitleMaxHistory);
        typeof(RealtimeBlockWindow).GetField("_isLoaded", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(window, true);
        return window;
    }

    private static List<Border> RenderedTexts(RealtimeBlockWindow window)
    {
        var canvas = (Canvas)window.FindName("TextCanvas");
        return canvas.Children.OfType<Border>().ToList();
    }

    private static string TextOf(Border border) => border.Child switch
    {
        TextBlock tb => tb.Text,
        Viewbox { Child: TextBlock tb } => tb.Text,
        _ => throw new InvalidOperationException($"Unexpected child {border.Child?.GetType().Name}")
    };

    [Fact]
    public void SubtitleMode_StacksOldAboveNew_EvictsMaxHistory_AndKeepsSpacingInsideBounds() => OnSta(() =>
    {
        var window = CreateLoadedWindow(height: 300, mode: RealtimeBlockMode.Subtitle, subtitleHoldMs: 3000, subtitleMaxHistory: 3);
        try
        {
            window.SetLines([Line("第一句")]);
            window.SetLines([Line("第二句")]);
            window.SetLines([Line("第三句")]);
            window.SetLines([Line("第四句")]);

            var borders = RenderedTexts(window);
            Assert.Equal(["第二句", "第三句", "第四句"], borders.Select(TextOf));

            for (int i = 0; i < borders.Count; i++)
            {
                double top = Canvas.GetTop(borders[i]);
                Assert.True(top >= 0, $"Item {i} top {top} overflowed window top");
                if (i > 0)
                {
                    double prevBottom = Canvas.GetTop(borders[i - 1]) + borders[i - 1].Height;
                    Assert.True(top > prevBottom, $"Item {i} top {top} overlapped previous bottom {prevBottom}");
                }
            }
        }
        finally { window.Close(); }
    });

    [Fact]
    public void SubtitleMode_RefreshesTailWithoutDuplicating_AndDropsOverflowingHistoryInTightWindow() => OnSta(() =>
    {
        var window = CreateLoadedWindow(height: 90, mode: RealtimeBlockMode.Subtitle, subtitleHoldMs: 3000, subtitleMaxHistory: 3);
        try
        {
            window.SetLines([Line("第一句", top: 45, height: 36)]);
            window.SetLines([Line("第一句", top: 45, height: 36)]);
            Assert.Equal(["第一句"], RenderedTexts(window).Select(TextOf));

            window.SetLines([Line("第二句", top: 45, height: 36)]);
            window.SetLines([Line("第三句", top: 45, height: 36)]);

            var borders = RenderedTexts(window);
            Assert.All(borders, b => Assert.True(Canvas.GetTop(b) >= 0));
            Assert.Equal("第三句", TextOf(borders[^1]));
            Assert.True(borders.Count < 3);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void SubtitleMode_EmptyUpdateKeepsUnexpiredUntilExpiry() => OnSta(() =>
    {
        var window = CreateLoadedWindow(height: 300, mode: RealtimeBlockMode.Subtitle, subtitleHoldMs: 3000, subtitleMaxHistory: 3);
        try
        {
            window.SetLines([Line("第一句")]);
            window.SetLines([Line("第二句")]);
            window.SetLines([]);

            Assert.Equal(["第一句", "第二句"], RenderedTexts(window).Select(TextOf));

            var future = DateTime.UtcNow.AddMilliseconds(4000);
            window.PruneExpiredSubtitles(future);

            Assert.Empty(RenderedTexts(window));
        }
        finally { window.Close(); }
    });

    [Fact]
    public void SubtitleHoldZero_AndPanelMode_ClearImmediatelyWithoutAccumulation() => OnSta(() =>
    {
        var zeroHold = CreateLoadedWindow(height: 300, mode: RealtimeBlockMode.Subtitle, subtitleHoldMs: 0, subtitleMaxHistory: 3);
        try
        {
            zeroHold.SetLines([Line("第一句")]);
            zeroHold.SetLines([Line("第二句")]);
            Assert.Equal(["第二句"], RenderedTexts(zeroHold).Select(TextOf));
            zeroHold.SetLines([]);
            Assert.Empty(RenderedTexts(zeroHold));
        }
        finally { zeroHold.Close(); }

        var panelWindow = CreateLoadedWindow(height: 300, mode: RealtimeBlockMode.Panel, subtitleHoldMs: 3000, subtitleMaxHistory: 3);
        try
        {
            panelWindow.SetLines([Line("項目一", readAs: RealtimeBlockMode.Panel)]);
            panelWindow.SetLines([Line("項目二", readAs: RealtimeBlockMode.Panel)]);
            Assert.Equal(["項目二"], RenderedTexts(panelWindow).Select(TextOf));
            panelWindow.SetLines([]);
            Assert.Empty(RenderedTexts(panelWindow));
        }
        finally { panelWindow.Close(); }
    });

    [Fact]
    public void AutoMode_AppliesOnlyToSubtitleReadAs_AndClearEmptiesQueue() => OnSta(() =>
    {
        var window = CreateLoadedWindow(height: 300, mode: RealtimeBlockMode.Auto, subtitleHoldMs: 3000, subtitleMaxHistory: 3);
        try
        {
            window.SetLines([Line("字幕一", readAs: RealtimeBlockMode.Subtitle)]);
            window.SetLines([Line("字幕二", readAs: RealtimeBlockMode.Subtitle)]);
            Assert.Equal(["字幕一", "字幕二"], RenderedTexts(window).Select(TextOf));

            window.SetLines([Line("選單項目", readAs: RealtimeBlockMode.Panel)]);
            Assert.Equal(["選單項目"], RenderedTexts(window).Select(TextOf));

            window.SetLines([Line("字幕三", readAs: RealtimeBlockMode.Subtitle)]);
            Assert.Equal(["字幕三"], RenderedTexts(window).Select(TextOf));
            window.Clear();
            Assert.Empty(RenderedTexts(window));
        }
        finally { window.Close(); }
    });

    [Fact]
    public void SubtitleMode_DeduplicatesPunctuationJitter_TypewriterGrowth_EarlierQueue_AndMultiBlockOverlap() => OnSta(() =>
    {
        var window = CreateLoadedWindow(height: 300, mode: RealtimeBlockMode.Subtitle, subtitleHoldMs: 3000, subtitleMaxHistory: 3);
        try
        {
            // 1. Punctuation/whitespace variation & same OriginalText updates tail in place
            window.SetLines([new TranslatedBlock("おはよう。", "早上好！", new Rect(40, 220, 520, 36))]);
            window.SetLines([new TranslatedBlock("おはよう", "早 上 好。", new Rect(40, 220, 520, 36))]);
            Assert.Equal(["早 上 好。"], RenderedTexts(window).Select(TextOf));

            // 2. Typewriter growth updates tail in place instead of stacking partial + full sentence
            window.SetLines([new TranslatedBlock("今日はどこへ", "今天要去哪里", new Rect(40, 220, 520, 36))]);
            window.SetLines([new TranslatedBlock("今日はどこへ行くの？", "今天到底要去哪里玩呢？", new Rect(40, 220, 520, 36))]);
            Assert.Equal(["早 上 好。", "今天到底要去哪里玩呢？"], RenderedTexts(window).Select(TextOf));

            // 3. Returning to an earlier sentence already in queue moves/refreshes instead of duplicating
            window.SetLines([new TranslatedBlock("おはよう", "早上好", new Rect(40, 220, 520, 36))]);
            Assert.Equal(["今天到底要去哪里玩呢？", "早上好"], RenderedTexts(window).Select(TextOf));

            // 4. Multi-block overlap ([A] -> [A, B]) & intra-frame duplicate block dedup
            window.SetLines([
                new TranslatedBlock("おはよう", "早上好", new Rect(40, 180, 520, 36)),
                new TranslatedBlock("おはよう", "早上好！", new Rect(40, 180, 520, 36)),
                new TranslatedBlock("いい天気", "天气真好", new Rect(40, 220, 520, 36)),
            ]);
            Assert.Equal(["今天到底要去哪里玩呢？", "早上好", "天气真好"], RenderedTexts(window).Select(TextOf));

            // 5. Same speaker name across two distinct lines must NOT falsely collapse
            window.Clear();
            window.SetLines([
                new TranslatedBlock("アリス", "爱丽丝", new Rect(40, 180, 120, 36)),
                new TranslatedBlock("こんにちは", "你好呀", new Rect(40, 220, 520, 36)),
            ]);
            window.SetLines([
                new TranslatedBlock("アリス", "爱丽丝", new Rect(40, 180, 120, 36)),
                new TranslatedBlock("さようなら", "再见了", new Rect(40, 220, 520, 36)),
            ]);
            Assert.Equal(["爱丽丝", "你好呀", "爱丽丝", "再见了"], RenderedTexts(window).Select(TextOf));
        }
        finally { window.Close(); }
    });

    [Fact]
    public void LateSubtitle_InsertsAboveNewerWithoutOverwriting_AndRespectsDeduplication() => OnSta(() =>
    {
        var window = CreateLoadedWindow(height: 300, mode: RealtimeBlockMode.Subtitle, subtitleHoldMs: 3000, subtitleMaxHistory: 3);
        try
        {
            window.SetLines([Line("第二句")]);
            window.SetLines([Line("迟到的第一句")], isLate: true);
            Assert.Equal(["迟到的第一句", "第二句"], RenderedTexts(window).Select(TextOf));

            // Duplicate late arrival does not add again
            window.SetLines([Line("迟到的第一句")], isLate: true);
            Assert.Equal(["迟到的第一句", "第二句"], RenderedTexts(window).Select(TextOf));
        }
        finally { window.Close(); }
    });

    private static void OnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
