namespace TaskbarLyrics.App;

/// <summary>
/// 水平滑动过渡共用的缓动与时长。五次 smootherstep，一阶与二阶导数在两端为零；
/// 图标增减的位移通常只有几十个逻辑像素，在约 85Hz 的实测帧率下每帧跨度本就很密，
/// 混入线性分量只会让起步更急，反而破坏收尾的柔和感。
/// </summary>
/// <remarks>
/// 任务栏嵌入（<see cref="EmbeddedTaskbarAnchor"/>）与悬浮窗口
/// （<see cref="MainWindow"/> 的跟随轮询）共用同一份参数，保证两种显示方式手感一致。
/// </remarks>
internal static class HorizontalSlideEasing
{
    /// <summary>
    /// 滑动时长。实测渲染节拍约 85Hz（受 DWM 对跨进程子窗口的合成节奏限制，非本进程可调），
    /// 280ms 约 24 帧，对图标增减这类小位移已经足够顺滑；再长会显得拖沓。
    /// </summary>
    public const double DurationMilliseconds = 280;

    public static double Evaluate(double progress)
    {
        var t = Math.Clamp(progress, 0, 1);
        return t * t * t * (t * ((t * 6) - 15) + 10);
    }
}
