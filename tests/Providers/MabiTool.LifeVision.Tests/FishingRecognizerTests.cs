using MabiTool.Contracts;
using MabiTool.Infrastructure.Workers;
using MabiTool.LifeVision.Worker;
using Xunit;

namespace MabiTool.LifeVision.Tests;

public sealed class FishingRecognizerTests
{
    private static FishingRecognizer NewRecognizer() => new(TestAssets.Catalog);

    [Fact]
    public void ConfirmDialogFixtureReportsFocusedDialogWithClickPointsInsideTheButtons()
    {
        var observation = NewRecognizer().Recognize(TestAssets.Fixture("fishing-mode-confirm-user.png"), 1000);
        var dialog = Assert.IsType<FishingDialogObservation>(observation.Dialog);
        Assert.Equal("focused", dialog.Kind);
        // 期望按钮框中心：确认 (752,864)，取消 (530,864)；用户实采帧的版式与登记一致，点击点落在这些框的中心附近。
        Assert.InRange(dialog.Confirm.X, 740, 764);
        Assert.InRange(dialog.Confirm.Y, 852, 876);
        Assert.InRange(dialog.Cancel.X, 518, 542);
        Assert.InRange(dialog.Cancel.Y, 852, 876);
        Assert.Null(observation.Hook);
        Assert.Null(observation.Gauge);
        Assert.False(observation.SceneVisible);
        Assert.False(observation.DismountVisible);
        Assert.Equal(1000, observation.CapturedAtMilliseconds);
    }

    [Fact]
    public void MountedFixtureReportsDismountWithoutHookGaugeOrDialog()
    {
        var observation = NewRecognizer().Recognize(TestAssets.Fixture("fishing-mounted-current.png"), 0);
        Assert.True(observation.DismountVisible);
        Assert.True(observation.SceneVisible);
        Assert.Null(observation.Hook);
        Assert.Null(observation.Gauge);
        Assert.Null(observation.Dialog);
        Assert.False(observation.BackpackFull);
    }

    [Fact]
    public void HookAndGaugeAreReadFromTheTrackBelowTheIcon()
    {
        var observation = NewRecognizer().Recognize(TestAssets.HookFrame(hook: true, fill: 38), 0);
        var hook = Assert.IsType<FishingHookObservation>(observation.Hook);
        Assert.Equal(new VisionPoint(630, 371), hook.Center);
        Assert.InRange(hook.Score, .9, 1);
        var gauge = Assert.IsType<FishingGaugeObservation>(observation.Gauge);
        Assert.Equal("hook-track", gauge.Source);
        Assert.Equal(38, gauge.FillPixels);
        Assert.Equal(96, gauge.TrackPixels);
        Assert.Equal(.4, gauge.Value, .01);
        Assert.Null(observation.Dialog);
    }

    [Fact]
    public void GaugeIsRememberedWhileTheHookIsMissedAndLocatedWhenNothingIsRemembered()
    {
        var recognizer = NewRecognizer();
        Assert.Equal("hook-track", recognizer.Recognize(TestAssets.HookFrame(true, 38), 0).Gauge!.Source);
        // 图标漏识别：沿用最近有效位置读数。
        var remembered = recognizer.Recognize(TestAssets.HookFrame(false, 38), 100);
        Assert.Null(remembered.Hook);
        Assert.Equal("remembered", remembered.Gauge!.Source);
        Assert.Equal(38, remembered.Gauge.FillPixels);
        // 没有记忆时只做定位：同一条形被定位为 located，宽度与记忆框一致。
        var located = NewRecognizer().Recognize(TestAssets.HookFrame(false, 38), 0);
        Assert.Equal("located", located.Gauge!.Source);
        Assert.Equal(38, located.Gauge.FillPixels);
        Assert.Equal(96, located.Gauge.TrackPixels);
    }

    [Theory]
    [InlineData(2900, "remembered")]
    [InlineData(3200, null)]
    public void RememberedGaugeIsForgottenAfterThreeSecondsWithoutSignal(double at, string? source)
    {
        var recognizer = NewRecognizer();
        recognizer.Recognize(TestAssets.HookFrame(true, 38), 0);
        Assert.Null(recognizer.Recognize(TestAssets.HookFrame(false, -1), 100).Gauge);
        // 空槽整条：记忆期内读成 0；记忆清空后只做定位，没有填充的条形不会被定位，普通场景的空槽色不会读成零。
        var observation = recognizer.Recognize(TestAssets.HookFrame(false, 0), at);
        Assert.Equal(source, observation.Gauge?.Source);
        if (source is not null) Assert.Equal(0, observation.Gauge!.FillPixels);
    }

    [Fact]
    public void DarkAndPlainFramesProduceNoGauge()
    {
        // 有记忆时，暗色与纯黑帧读不出读数（记忆框读到的行不是“填充 + 空槽”）。
        var recognizer = NewRecognizer();
        recognizer.Recognize(TestAssets.HookFrame(true, 38), 0);
        foreach (var (frame, at) in new[] { (TestAssets.Black(), 200d), (TestAssets.Solid(1280, 960, 20, 20, 20), 400) })
        {
            var observation = recognizer.Recognize(frame, at);
            Assert.Null(observation.Gauge);
            Assert.Null(observation.Hook);
        }
        // 没有记忆也没有上钩图标时只做定位：整帧的空槽色或填充色不是有形的双色条，普通场景的全空颜色不会读成零。
        foreach (var color in new[] { TestAssets.EmptyColor, TestAssets.FillColor })
        {
            var observation = NewRecognizer().Recognize(TestAssets.Solid(1280, 960, color[0], color[1], color[2]), 0);
            Assert.Null(observation.Gauge);
            Assert.Null(observation.Hook);
        }
    }

    [Fact]
    public void SceneIsVisibleOnlyWhenBagIconAndMapKeyBothMatch()
    {
        var both = TestAssets.Black();
        TestAssets.PasteTemplate(both, FishingProfile.SceneBag);
        TestAssets.PasteTemplate(both, FishingProfile.SceneMap);
        Assert.True(NewRecognizer().Recognize(both, 0).SceneVisible);
        var bagOnly = TestAssets.Black();
        TestAssets.PasteTemplate(bagOnly, FishingProfile.SceneBag);
        Assert.False(NewRecognizer().Recognize(bagOnly, 0).SceneVisible);
    }

    [Fact]
    public void BackpackFullToastIsCheckedEveryThreeHundredMillisecondsAndHeldForOneAndAHalfSeconds()
    {
        var recognizer = NewRecognizer();
        var toast = TestAssets.Black();
        TestAssets.PasteTemplate(toast, FishingProfile.BackpackFull);
        var plain = TestAssets.Black();
        Assert.True(recognizer.Recognize(toast, 0).BackpackFull);
        Assert.True(recognizer.Recognize(plain, 100).BackpackFull);
        Assert.True(recognizer.Recognize(plain, 1400).BackpackFull);
        Assert.False(recognizer.Recognize(plain, 1500).BackpackFull);
        // 上次检查在 1400：1700 之前带提示的帧不会被检查到。
        Assert.False(recognizer.Recognize(toast, 1600).BackpackFull);
        Assert.True(recognizer.Recognize(toast, 1800).BackpackFull);
    }

    [Theory]
    [InlineData("focused", 0, 0)]
    [InlineData("focused", 5, -4)]
    [InlineData("leisure", 0, 0)]
    [InlineData("leisure", -6, 6)]
    [InlineData("current", 0, 0)]
    public void DialogTitlesGiveButtonPointsThatFollowTheTitlePosition(string which, int offsetX, int offsetY)
    {
        var spec = which switch { "focused" => FishingProfile.FocusedDialog, "leisure" => FishingProfile.LeisureDialog, _ => FishingProfile.FocusedDialogCurrent };
        var frame = TestAssets.Black();
        TestAssets.PasteTemplate(frame, spec, offsetX, offsetY);
        // 弹窗帧里同时有场景图标与坐骑图标也只看弹窗。
        TestAssets.PasteTemplate(frame, FishingProfile.SceneBag);
        TestAssets.PasteTemplate(frame, FishingProfile.Dismount);
        var observation = NewRecognizer().Recognize(frame, 0);
        var dialog = Assert.IsType<FishingDialogObservation>(observation.Dialog);
        Assert.Equal(which == "leisure" ? "leisure" : "focused", dialog.Kind);
        Assert.Equal(new VisionPoint(752 + offsetX, 864 + offsetY), dialog.Confirm);
        Assert.Equal(new VisionPoint(530 + offsetX, 864 + offsetY), dialog.Cancel);
        Assert.False(observation.SceneVisible);
        Assert.False(observation.DismountVisible);
        Assert.Null(observation.Hook);
        Assert.Null(observation.Gauge);
    }

    [Fact]
    public void BackpackFullToastIsStillReportedWhileADialogIsShown()
    {
        var frame = TestAssets.Black();
        TestAssets.PasteTemplate(frame, FishingProfile.FocusedDialog);
        TestAssets.PasteTemplate(frame, FishingProfile.BackpackFull);
        var observation = NewRecognizer().Recognize(frame, 0);
        Assert.NotNull(observation.Dialog);
        Assert.True(observation.BackpackFull);
    }

    [Fact]
    public void FramesOfAnyOtherSizeAreRejected()
    {
        var error = Assert.Throws<WorkerException>(() => NewRecognizer().Recognize(TestAssets.Solid(1920, 1080, 0, 0, 0), 0));
        Assert.Equal("life.fishing.layoutUnsupported", error.Code);
    }

    public static TheoryData<string> TemplateIds => new(FishingProfile.Templates.Select(spec => spec.Id));

    [Theory]
    [MemberData(nameof(TemplateIds))]
    public void EachTemplatePastedAtItsBoxMatchesAboveItsThreshold(string id)
    {
        var spec = FishingProfile.Templates.Single(item => item.Id == id);
        // 与识别器的调用参数一致：场景图标使用放宽到 0.85 的阈值，其余用默认下限。
        var scene = spec == FishingProfile.SceneBag || spec == FishingProfile.SceneMap;
        double? thresholdOverride = scene ? FishingProfile.SceneThreshold : null;
        var minimum = scene ? FishingProfile.SceneThreshold : FishingProfile.DefaultMinimum;
        var frame = TestAssets.Black();
        TestAssets.PasteTemplate(frame, spec);
        var matcher = new TemplateMatcher(TestAssets.Catalog);
        var hit = matcher.Match(frame, spec, minimum, true, thresholdOverride);
        Assert.NotNull(hit);
        Assert.Equal(spec.Box, hit.Value.Box);
        Assert.True(hit.Value.Score >= TemplateMatcher.ResolveThreshold(spec, minimum, thresholdOverride), $"{spec.Id} {hit.Value.Score}");
        Assert.Null(matcher.Match(TestAssets.Black(), spec, minimum, true, thresholdOverride));
    }

    [Fact]
    public void ThresholdRuleUsesGlyphThresholdsAndCapsLooseGlyphMatchesAtPointNineThree()
    {
        Assert.Equal(.90, TemplateMatcher.ResolveThreshold(FishingProfile.Hook, .97, null));
        Assert.Equal(.97, TemplateMatcher.ResolveThreshold(FishingProfile.FocusedDialog, .97, null));
        Assert.Equal(.97, TemplateMatcher.ResolveThreshold(FishingProfile.Dismount, .97, null));
        Assert.Equal(.85, TemplateMatcher.ResolveThreshold(FishingProfile.SceneBag, .85, .85));
        Assert.Equal(.85, TemplateMatcher.ResolveThreshold(FishingProfile.SceneMap, .85, .85));
        // 没有字形阈值的字形类模板：不超过 0.93；灰度模板不受该上限约束。
        Assert.Equal(.93, TemplateMatcher.ResolveThreshold(FishingProfile.SceneMap, .97, null));
        Assert.Equal(.97, TemplateMatcher.ResolveThreshold(FishingProfile.SceneBag, .97, null));
    }

    [Fact]
    public void TwoSeparateCopiesOfTheHookAreAmbiguousAndNothingIsReported()
    {
        var matcher = new TemplateMatcher(TestAssets.Catalog);
        var frame = TestAssets.Black();
        TestAssets.PasteTemplate(frame, FishingProfile.Hook);
        Assert.NotNull(matcher.Match(frame, FishingProfile.Hook));
        TestAssets.Paste(frame, TestAssets.Template(FishingProfile.Hook), 700, 420);
        Assert.Null(matcher.Match(frame, FishingProfile.Hook));
    }
}
