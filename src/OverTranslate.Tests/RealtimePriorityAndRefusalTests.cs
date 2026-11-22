using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using OverTranslate.Models;
using OverTranslate.Services;
using OverTranslate.Services.Ocr;
using OverTranslate.Services.Providers;
using OverTranslate.Services.Realtime;
using Xunit;

namespace OverTranslate.Tests;

public class RealtimePriorityAndRefusalTests
{
    [Fact]
    public async Task PriorityInferenceGate_WakesHighPrioritySubtitleBeforeLowPriorityPanel()
    {
        var gate = new OnnxOcrEngine.PriorityInferenceGate(1);
        await gate.AcquireAsync(highPriority: false, CancellationToken.None);

        var order = new List<string>();
        var lowWaiting = gate.AcquireAsync(highPriority: false, CancellationToken.None)
            .ContinueWith(_ => { lock (order) order.Add("low"); gate.Release(); });
        var highWaiting = gate.AcquireAsync(highPriority: true, CancellationToken.None)
            .ContinueWith(_ => { lock (order) order.Add("high"); gate.Release(); });

        // Non-subtitle TryAcquire returns false while high-priority subtitle is queued
        Assert.False(await gate.TryAcquireAsync(highPriority: false, CancellationToken.None));

        gate.Release();
        await Task.WhenAll(highWaiting, lowWaiting);

        Assert.Equal(["high", "low"], order);
    }

    [Fact]
    public void RegionTranslationPump_PreservesLateSubtitleAndEmptyClear_WhilePanelDropsOvertaken()
    {
        var session = new RealtimeTranslationSession(new OcrService(), new TranslationService());
        typeof(RealtimeTranslationSession)
            .GetField("_subtitleHoldMs", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(session, 3000);

        var updates = new List<RealtimeRegionUpdate>();
        session.RegionUpdated += (_, update) => updates.Add(update);

        TranslatedBlock Block(string text, RealtimeBlockMode? readAs = null) =>
            new("src:" + text, text, new System.Windows.Rect(0, 0, 100, 30)) { ReadAs = readAs };

        // 1. Subtitle mode: empty pass 2 does NOT block in-flight pass 1; late pass 1 after pass 3 is marked IsLate
        var subtitleRegion = new RealtimeRegion(0, new System.Drawing.Rectangle(0, 0, 500, 200), RealtimeBlockMode.Subtitle);
        var subtitleFollow = new RealtimeScrollFollow(0, (lines, gen) =>
            typeof(RealtimeTranslationSession)
                .GetMethod("RaiseRegionUpdated", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(session, [new RealtimeRegionUpdate(0, lines, gen)]));
        var subtitlePump = new RealtimeTranslationSession.RegionTranslationPump(
            session, subtitleRegion, subtitleFollow, "JA", "ZH-HANS", new SemaphoreSlim(4, 4), 4, CancellationToken.None);

        var pass1 = subtitlePump.NextPass();
        var pass2Empty = subtitlePump.NextPass();
        Assert.True(subtitlePump.Publish(pass2Empty, []));
        Assert.True(subtitlePump.Publish(pass1, [Block("第一句")]));
        Assert.False(updates[^1].IsLate);

        var pass3 = subtitlePump.NextPass();
        var pass4 = subtitlePump.NextPass();
        Assert.True(subtitlePump.Publish(pass4, [Block("第三句")]));
        Assert.True(subtitlePump.Publish(pass3, [Block("迟到的第二句")]));
        Assert.True(updates[^1].IsLate);
        Assert.Equal("迟到的第二句", updates[^1].Lines[0].TranslatedText);

        // 2. Panel mode: strict order preserved — overtaken pass is dropped
        var panelRegion = new RealtimeRegion(1, new System.Drawing.Rectangle(0, 0, 500, 200), RealtimeBlockMode.Panel);
        var panelFollow = new RealtimeScrollFollow(1, (_, _) => { });
        var panelPump = new RealtimeTranslationSession.RegionTranslationPump(
            session, panelRegion, panelFollow, "JA", "ZH-HANS", new SemaphoreSlim(4, 4), 4, CancellationToken.None);

        var p1 = panelPump.NextPass();
        var p2 = panelPump.NextPass();
        Assert.True(panelPump.Publish(p2, []));
        Assert.False(panelPump.Publish(p1, [Block("菜单", RealtimeBlockMode.Panel)]));
    }

    [Fact]
    public async Task RealtimeSession_RefusalFromProvider_StripsContextAndNeverPoisonsCacheOrHistory()
    {
        var server = new OpenAiFakeServer
        {
            Answer = text => text == "Kill" ? "抱歉，我无法满足该请求" : "你好",
        };
        using var http = new HttpClient(server);
        var openAiTranslation = new TranslationService();
        var fakeOpenAi = new OpenAiCompatibleProvider(
            http,
            () => new OpenAiCompatibleOptions("http://127.0.0.1:1/v1", "test-model", Batch: false));
        typeof(TranslationService)
            .GetField("_openAi", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(openAiTranslation, fakeOpenAi);

        var openAiSession = new RealtimeTranslationSession(new OcrService(), openAiTranslation);
        typeof(RealtimeTranslationSession)
            .GetField("_provider", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(openAiSession, TranslationProvider.OpenAI);
        var openAiHistory = (RealtimeTranslationHistory)typeof(RealtimeTranslationSession)
            .GetField("_history", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(openAiSession)!;
        openAiHistory.Add([new TranslationPair("おはよう", "早上好")], () => true);

        var translateMethod = typeof(RealtimeTranslationSession)
            .GetMethod("TranslateAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var blocks = new List<OcrTextBlock>
        {
            new("こんにちは", new System.Windows.Rect(0, 0, 100, 30)),
            new("Kill", new System.Windows.Rect(0, 40, 100, 30)),
        };
        var result = await (Task<List<TranslatedBlock>?>)translateMethod.Invoke(
            openAiSession, [blocks, "JA", "ZH-HANS", false, 0, CancellationToken.None])!;

        Assert.NotNull(result);
        Assert.Equal("你好", result[0].TranslatedText);
        Assert.False(result[0].Untranslated);
        Assert.True(result[1].Untranslated);
        Assert.Equal("Kill", result[1].TranslatedText);

        var killRequests = server.Requests.Where(r => r.Text == "Kill").ToList();
        Assert.Equal(2, killRequests.Count);
        Assert.Contains("おはよう", killRequests[0].Message);
        Assert.DoesNotContain("おはよう", killRequests[1].Message);

        var cache = (RealtimeTranslationCache)typeof(RealtimeTranslationSession)
            .GetField("_translationCache", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(openAiSession)!;
        Assert.True(cache.TryGet("OpenAI|JA|ZH-HANS||こんにちは", 0, out _));
        Assert.False(cache.TryGet("OpenAI|JA|ZH-HANS||Kill", 0, out _));
        Assert.Equal(["おはよう", "こんにちは"], openAiHistory.Lines.Select(l => l.Source).ToList());
    }
}
