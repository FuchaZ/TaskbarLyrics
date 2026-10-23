using Xunit;

namespace TaskbarLyrics.App.Tests;

public sealed class TaskbarIconRegionTests
{
    private const double IconGap = EmbeddedTaskbarLayoutCalculator.IconGap;

    [Fact]
    public void ToLogicalConvertsPhysicalPixelsAndMovesOriginToTaskbarClientArea()
    {
        var region = new TaskbarIconRegion(1960, 2600);

        var anchors = region.ToLogical(2, 1000);

        Assert.Equal(480, anchors.LeftIconsRight);
        Assert.Equal(800, anchors.TrayLeft);
    }

    [Fact]
    public void ToLogicalKeepsUnavailableBoundariesUnavailable()
    {
        var anchors = TaskbarIconRegion.Unavailable.ToLogical(1.5, 1920);

        Assert.Null(anchors.LeftIconsRight);
        Assert.Null(anchors.TrayLeft);
    }

    [Fact]
    public void ToLogicalTreatsInvalidScaleAsOne()
    {
        var anchors = new TaskbarIconRegion(1050, null).ToLogical(0, 1000);

        Assert.Equal(50, anchors.LeftIconsRight);
        Assert.Null(anchors.TrayLeft);
    }

    [Theory]
    [InlineData(0, 408)]
    [InlineData(10, 418)]
    [InlineData(-100, 308)]
    public void AutoFollowSitsAfterIconGroupWithConfiguredGap(double xOffset, double expected)
    {
        var anchors = new TaskbarHorizontalAnchors(400, 1200);

        Assert.Equal(expected, EmbeddedTaskbarLayoutCalculator.CalculateAutoFollowLeft(0, 1600, 420, xOffset, anchors), 10);
    }

    [Fact]
    public void AutoFollowStopsBeforeTray()
    {
        var anchors = new TaskbarHorizontalAnchors(900, 1000);

        // 图标右缘 + 间隙 + 偏移会压到托盘，必须被托盘左缘挡回。
        Assert.Equal(1000 - 420 - IconGap, EmbeddedTaskbarLayoutCalculator.CalculateAutoFollowLeft(0, 1600, 420, 0, anchors));
    }

    [Fact]
    public void AutoFollowFallsBackToContainerLeftWhenBoundariesAreUnknown()
    {
        var anchors = TaskbarHorizontalAnchors.Unavailable;

        Assert.Equal(1920, EmbeddedTaskbarLayoutCalculator.CalculateAutoFollowLeft(1920, 3840, 420, 0, anchors));
        Assert.Equal(1950, EmbeddedTaskbarLayoutCalculator.CalculateAutoFollowLeft(1920, 3840, 420, 30, anchors));
    }

    [Fact]
    public void AutoFollowClampsWindowInsideContainer()
    {
        var rightOverflow = new TaskbarHorizontalAnchors(3000, null);

        Assert.Equal(
            1600 - 420,
            EmbeddedTaskbarLayoutCalculator.CalculateAutoFollowLeft(0, 1600, 420, 0, rightOverflow));
        Assert.Equal(
            -500,
            EmbeddedTaskbarLayoutCalculator.CalculateAutoFollowLeft(-500, 1600, 420, -100, TaskbarHorizontalAnchors.Unavailable));
    }

    [Fact]
    public void AutoFollowKeepsXOffsetWhenWindowWouldOverflowOnlyBecauseOfRounding()
    {
        // 回归：夹紧只发生一次。窗口宽度带来的亚像素差不应让 X 偏移失效。
        var anchors = new TaskbarHorizontalAnchors(400, null);

        var atLimit = EmbeddedTaskbarLayoutCalculator.CalculateAutoFollowLeft(0, 1600, 420, 0, anchors);
        var shifted = EmbeddedTaskbarLayoutCalculator.CalculateAutoFollowLeft(0, 1600, 420, 20, anchors);

        Assert.Equal(20, shifted - atLimit, 10);
    }

    [Fact]
    public void OccupiedIntervalsMergeAdjacentButtonsIntoTheFirstCluster()
    {
        // 回归：实机（125% 缩放）上相邻应用按钮的区间是无缝衔接的（0-69、69-124…），
        // 第一簇的右缘必须随合并生长到最后一个控件，而不是停在第一个控件的右缘。
        var occupied = new[]
        {
            new TaskbarInterval(0, 69),
            new TaskbarInterval(69, 124),
            new TaskbarInterval(124, 179),
            new TaskbarInterval(179, 234),
            new TaskbarInterval(234, 289),
            new TaskbarInterval(2199, 2239),
            new TaskbarInterval(2239, 2279),
            new TaskbarInterval(2279, 2334)
        };

        var anchors = TaskbarHorizontalAnchors.FromOccupiedIntervals(occupied);

        Assert.Equal(289, anchors.LeftIconsRight);
        Assert.Equal(2199, anchors.TrayLeft);
    }

    [Fact]
    public void OccupiedIntervalsKeepSeparateClustersWhenGapExceedsTolerance()
    {
        var occupied = new[]
        {
            new TaskbarInterval(0, 69),
            new TaskbarInterval(150, 200),
            new TaskbarInterval(1000, 1040)
        };

        var anchors = TaskbarHorizontalAnchors.FromOccupiedIntervals(occupied);

        Assert.Equal(69, anchors.LeftIconsRight);
        Assert.Equal(1000, anchors.TrayLeft);
    }

    [Fact]
    public void OccupiedIntervalsWithSingleClusterAreUnavailable()
    {
        var occupied = new[]
        {
            new TaskbarInterval(0, 69),
            new TaskbarInterval(69, 400)
        };

        var anchors = TaskbarHorizontalAnchors.FromOccupiedIntervals(occupied);

        Assert.Null(anchors.LeftIconsRight);
        Assert.Null(anchors.TrayLeft);
    }

    [Fact]
    public void OccupiedIntervalsIgnoreInvalidEntriesAndEmptyInput()
    {
        Assert.Equal(TaskbarHorizontalAnchors.Unavailable, TaskbarHorizontalAnchors.FromOccupiedIntervals([]));

        var occupied = new[]
        {
            new TaskbarInterval(50, 50),
            new TaskbarInterval(30, 20),
            new TaskbarInterval(double.NaN, 100),
            new TaskbarInterval(0, 69),
            new TaskbarInterval(90, 130),
            new TaskbarInterval(1000, 1040)
        };

        var anchors = TaskbarHorizontalAnchors.FromOccupiedIntervals(occupied);

        Assert.Equal(130, anchors.LeftIconsRight);
        Assert.Equal(1000, anchors.TrayLeft);
    }
}
