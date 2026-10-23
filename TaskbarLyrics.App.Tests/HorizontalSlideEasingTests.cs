using Xunit;

namespace TaskbarLyrics.App.Tests;

public sealed class HorizontalSlideEasingTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(0.5, 0.5)]
    public void SlideEasingKeepsEndpointsAndMidpointFixed(double progress, double expected)
    {
        Assert.Equal(expected, SlideEasing(progress), 10);
    }

    [Fact]
    public void SlideEasingStartsAndEndsGently()
    {
        // 五次 smootherstep：两端斜率为 0，起步与收尾都没有速度突变。
        const double epsilon = 1e-3;

        var atStart = (SlideEasing(epsilon) - SlideEasing(0)) / epsilon;
        var atEnd = (SlideEasing(1) - SlideEasing(1 - epsilon)) / epsilon;
        var atMiddle = (SlideEasing(0.5 + epsilon) - SlideEasing(0.5)) / epsilon;

        Assert.True(atStart < atMiddle * 0.1, $"起步斜率 {atStart} 应远小于中段斜率 {atMiddle}");
        Assert.True(atEnd < atMiddle * 0.1, $"收尾斜率 {atEnd} 应远小于中段斜率 {atMiddle}");
    }

    [Fact]
    public void SlideEasingPeakSlopeAtMiddleIsTheSmootherstepValue()
    {
        // 中段斜率约 1.875，是整条曲线的最大速度，符合 smootherstep 的解析结果。
        const double epsilon = 1e-4;

        var atMiddle = (SlideEasing(0.5 + epsilon) - SlideEasing(0.5)) / epsilon;

        Assert.Equal(1.875, atMiddle, 1);
    }

    [Fact]
    public void SlideEasingIsMonotonic()
    {
        var previous = SlideEasing(0);
        for (var step = 1; step <= 100; step++)
        {
            var value = SlideEasing(step / 100.0);
            Assert.True(value >= previous, $"进度 {step}% 处出现回退");
            previous = value;
        }
    }

    [Theory]
    [InlineData(-0.5, 0)]
    [InlineData(1.5, 1)]
    public void SlideEasingClampsOutOfRangeProgress(double progress, double expected)
    {
        Assert.Equal(expected, SlideEasing(progress), 10);
    }

    private static double SlideEasing(double progress)
    {
        var t = Math.Clamp(progress, 0, 1);
        return t * t * t * (t * ((t * 6) - 15) + 10);
    }
}
