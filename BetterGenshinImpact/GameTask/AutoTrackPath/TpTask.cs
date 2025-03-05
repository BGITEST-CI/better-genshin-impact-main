﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.Core.Recognition.OpenCv;
using BetterGenshinImpact.Core.Script.Dependence;
using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.Core.Simulator.Extensions;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;
using BetterGenshinImpact.GameTask.AutoPathing;
using BetterGenshinImpact.GameTask.AutoPathing.Model;
using BetterGenshinImpact.GameTask.AutoPathing.Model.Enum;
using BetterGenshinImpact.GameTask.AutoTrackPath.Model;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Common.Element.Assets;
using BetterGenshinImpact.GameTask.Common.Exceptions;
using BetterGenshinImpact.GameTask.Common.Job;
using BetterGenshinImpact.GameTask.Common.Map;
using BetterGenshinImpact.GameTask.Model.Area;
using BetterGenshinImpact.GameTask.QuickTeleport.Assets;
using BetterGenshinImpact.Helpers.Extensions;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using Vanara.PInvoke;
using static BetterGenshinImpact.GameTask.Common.TaskControl;

namespace BetterGenshinImpact.GameTask.AutoTrackPath;

/// <summary>
/// 传送任务
/// </summary>
public class TpTask(CancellationToken ct)
{
    private readonly QuickTeleportAssets _assets = QuickTeleportAssets.Instance;
    private readonly Rect _captureRect = TaskContext.Instance().SystemInfo.ScaleMax1080PCaptureRect;
    private readonly double _zoomOutMax1080PRatio = TaskContext.Instance().SystemInfo.ZoomOutMax1080PRatio;
    private readonly TpConfig _tpConfig = TaskContext.Instance().Config.TpConfig;

    /// <summary>
    /// 直接通过缩放比例按钮计算放大按钮的Y坐标
    /// </summary>
    private readonly int _zoomInButtonY = TaskContext.Instance().Config.TpConfig.ZoomStartY - 24; //  y-coordinate for zoom-in button  = _zoomStartY - 24
    /// <summary>
    /// 直接通过缩放比例按钮计算缩小按钮的Y坐标
    /// </summary>
    private readonly int _zoomOutButtonY = TaskContext.Instance().Config.TpConfig.ZoomEndY + 24; //  y-coordinate for zoom-out button = _zoomEndY + 24
    
    private const double DisplayTpPointZoomLevel = 4.4; // 传送点显示的时候的地图比例

    /// <summary>
    /// 传送到七天神像
    /// </summary>
    public async Task TpToStatueOfTheSeven()
    {
        await CheckInBigMapUi();
        if (_tpConfig.MapZoomEnabled)
        {
            double currentZoomLevel = GetBigMapZoomLevel(CaptureToRectArea());
            if (currentZoomLevel > DisplayTpPointZoomLevel)
            {
                await AdjustMapZoomLevel(currentZoomLevel, DisplayTpPointZoomLevel);
            }
            else if (currentZoomLevel < 3)
            {
                await AdjustMapZoomLevel(currentZoomLevel, 3);
            }
        }
        string? country = _tpConfig.ReviveStatueOfTheSevenCountry;
        string? area = _tpConfig.ReviveStatueOfTheSevenArea;
        double x = _tpConfig.ReviveStatueOfTheSevenPointX;
        double y = _tpConfig.ReviveStatueOfTheSevenPointY;
        if (_tpConfig.IsReviveInNearestStatueOfTheSeven)
        {
            var center = GetBigMapCenterPoint();
            var giTpPoint = GetNearestGoddess(center.X, center.Y);
            if (giTpPoint != null)
            {
                country = giTpPoint.Country;
                area = giTpPoint.Area;
                x = giTpPoint.X;
                y = giTpPoint.Y;
            }
        }
        Logger.LogInformation("将传送至 {country} {area} 七天神像", country, area);
        await Tp(x, y);
        if (_tpConfig.ShouldMove || _tpConfig.IsReviveInNearestStatueOfTheSeven)
        {
            var waypoint = new Waypoint
            {
                X = x,
                Y = y,
                Type = WaypointType.Path.Code,
                MoveMode = MoveModeEnum.Walk.Code
            };
            var waypointForTrack = new WaypointForTrack(waypoint);
            await new PathExecutor(ct).MoveTo(waypointForTrack);
        }
        
        await Delay((int)(_tpConfig.HpRestoreDuration * 1000), ct);
    }

    /// <summary>
    /// 获取离 x,y 最近的七天神像
    /// </summary>
    /// <param name="x"></param>
    /// <param name="y"></param>
    /// <returns></returns>
    private GiTpPosition? GetNearestGoddess(double x, double y)
    {
        GiTpPosition? nearestGiTpPosition = null;
        double minDistance = double.MaxValue;
        foreach (var (_, goddessPosition) in MapLazyAssets.Instance.GoddessPositions)
        {
            var distance = Math.Sqrt(Math.Pow(goddessPosition.X - x, 2) + Math.Pow(goddessPosition.Y - y, 2));
            if (distance < minDistance)
            {
                minDistance = distance;
                nearestGiTpPosition = goddessPosition;
            }
        }
        // 获取最近的神像位置
        return nearestGiTpPosition;
    }
    
    /// <summary>
    /// 通过大地图传送到指定坐标最近的传送点，然后移动到指定坐标
    /// </summary>
    /// <param name="tpX"></param>
    /// <param name="tpY"></param>
    /// <param name="force">强制以当前的tpX,tpY坐标进行自动传送</param>
    private async Task<(double, double)> TpOnce(double tpX, double tpY, bool force = false)
    {
        // tp 前释放所有按键
        Simulation.ReleaseAllKey();
        await Delay(20, ct);
        // 1. 确认在地图界面
        await CheckInBigMapUi();

        // 2. 传送前的计算准备
        // 获取离目标传送点最近的两个传送点，按距离排序
        var nTpPoints = GetNearestNTpPoints(tpX, tpY, 2);
        // 获取最近的传送点与区域
        var (x, y, country) = force ? (tpX, tpY, null) : (nTpPoints[0].X, nTpPoints[0].Y, nTpPoints[0].Country);
        var disBetweenTpPoints = Math.Sqrt(Math.Pow(nTpPoints[0].X - nTpPoints[1].X, 2) +
                                           Math.Pow(nTpPoints[0].Y - nTpPoints[1].Y, 2));
        // 确保不会点错传送点的最小缩放，保证至少为 1.0
        var minZoomLevel = Math.Max(disBetweenTpPoints / 20, 1.0);
        // 计算传送点位置离哪个地图切换后的中心点最近，切换到该地图
        await SwitchRecentlyCountryMap(x, y, country);

        // 3. 调整初始缩放等级，避免识别中心点失败
        var zoomLevel = GetBigMapZoomLevel(CaptureToRectArea());
        if (_tpConfig.MapZoomEnabled)
        {
            /* 动态调整缩放逻辑：
                1. 如果当前缩放大于显示传送点级别 -> 缩小
                2. 如果小于配置的最小级别 -> 放大 */
            if (zoomLevel > DisplayTpPointZoomLevel + _tpConfig.PrecisionThreshold)
            {
                await AdjustMapZoomLevel(zoomLevel, DisplayTpPointZoomLevel);
                zoomLevel = DisplayTpPointZoomLevel;
                Logger.LogInformation("当前缩放等级过大，调整为 {zoomLevel:0.00}", DisplayTpPointZoomLevel);
            }
            else if (zoomLevel < _tpConfig.MinZoomLevel - _tpConfig.PrecisionThreshold)
            {
                await AdjustMapZoomLevel(zoomLevel, _tpConfig.MinZoomLevel);
                zoomLevel = _tpConfig.MinZoomLevel;
                Logger.LogInformation("当前缩放等级过小，调整为 {zoomLevel:0.00}", _tpConfig.MinZoomLevel);
            }
        }

        // 4. zoomLevel不满足条件，强制进行一次 MoveMapTo，避免传送点相近导致误点
        if (zoomLevel > minZoomLevel)
        {
            if (_tpConfig.MapZoomEnabled)
            {
                Logger.LogInformation("目标传送点有相近传送点，到目标传送点附近将缩放到{zoomLevel:0.00}", minZoomLevel);
                await MoveMapTo(x, y, minZoomLevel);
                await Delay(300, ct); // 等待地图移动完成
            }
            else
            {
                Logger.LogInformation("目标传送点有相近传送点，可能传送失败。如果失败请到设置-大地图地图传送设置开启地图缩放");
                // TODO 部分无法区分点位强制缩放，即使没有zoomEnabled。
            }
        }

        // 5. 判断传送点是否在当前界面，若否则移动地图
        var bigMapInAllMapRect = GetBigMapRect();
        var retryCount = 0;
        do
        {
            if (IsPointInBigMapWindow(bigMapInAllMapRect, x, y)) break;
            if (retryCount++ >= 5) // 防止死循环
            {
                Logger.LogWarning("多次尝试未移动到目标传送点，传送失败");
                throw new Exception("多次尝试未移动到目标传送点，传送失败");
            }

            Logger.LogInformation("传送点不在当前大地图范围内，重新调整地图位置");
            await MoveMapTo(x, y);
            await Delay(300, ct);
            bigMapInAllMapRect = GetBigMapRect();
        } while (true);

        // 6. 计算传送点位置并点击
        // Debug.WriteLine($"({x},{y}) 在 {bigMapInAllMapRect} 内，计算它在窗体内的位置");
        // 注意这个坐标的原点是中心区域某个点，所以要转换一下点击坐标（点击坐标是左上角为原点的坐标系），不能只是缩放
        var (clickX, clickY) = ConvertToGameRegionPosition(bigMapInAllMapRect, x, y);
        Logger.LogInformation("点击传送点");
        CaptureToRectArea().ClickTo((int)clickX, (int)clickY);

        // 7. 触发一次快速传送功能
        await Delay(500, ct);
        await ClickTpPoint(CaptureToRectArea());

        // 8. 等待传送完成
        await WaitForTeleportCompletion(50, 1200);
        return (x, y);
    }

    /// <summary>
    ///     检查传送是否完成，未完成则等待
    /// </summary>
    /// <param name="maxAttempts">最大检查延时的次数</param>
    /// <param name="delayMs">如果未完成加载，检查加载页面的延时。</param>
    private async Task WaitForTeleportCompletion(int maxAttempts, int delayMs)
    {
        await Delay(delayMs, ct);
        for (var i = 0; i < maxAttempts; i++)
        {
            using var capture = CaptureToRectArea();
            if (Bv.IsInMainUi(capture))
            {
                Logger.LogInformation("传送完成，返回主界面");
                return;
            }

            await Delay(delayMs, ct);
        }

        Logger.LogWarning("传送等待超时，换台电脑吧");
    }
    
    /// <summary>
    /// 传送点是否在大地图窗口内
    /// </summary>
    /// <param name="bigMapInAllMapRect">大地图在整个游戏地图中的矩形位置（原神坐标系）</param>
    /// <param name="x">传送点x坐标（原神坐标系）</param>
    /// <param name="y">传送点y坐标（原神坐标系）</param>
    /// <returns></returns>
    private bool IsPointInBigMapWindow(Rect bigMapInAllMapRect, double x, double y)
    {
        // 坐标不包含直接返回
        if (!bigMapInAllMapRect.Contains(x, y))
        {
            return false;
        }

        var (clickX, clickY) = ConvertToGameRegionPosition(bigMapInAllMapRect, x, y);
        // 屏蔽左上角360x400区域
        if (clickX < 360 * _zoomOutMax1080PRatio && clickY < 400 * _zoomOutMax1080PRatio)
        {
            return false;
        }

        // 屏蔽周围 115 一圈的区域
        if (clickX < 115 * _zoomOutMax1080PRatio
            || clickY < 115 * _zoomOutMax1080PRatio
            || clickX > _captureRect.Width - 115 * _zoomOutMax1080PRatio
            || clickY > _captureRect.Height - 115 * _zoomOutMax1080PRatio)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// 转换传送点坐标到窗体内需要点击的坐标
    /// </summary>
    /// <param name="bigMapInAllMapRect">大地图在整个游戏地图中的矩形位置（原神坐标系）</param>
    /// <param name="x">传送点x坐标（原神坐标系）</param>
    /// <param name="y">传送点y坐标（原神坐标系）</param>
    /// <returns></returns>
    private (double clickX, double clickY) ConvertToGameRegionPosition(Rect bigMapInAllMapRect, double x, double y)
    {
        var (picX, picY) = MapCoordinate.GameToMain2048(x, y);
        var picRect = MapCoordinate.GameToMain2048(bigMapInAllMapRect);
        Debug.WriteLine($"({picX},{picY}) 在 {picRect} 内，计算它在窗体内的位置");
        var clickX = (picX - picRect.X) / picRect.Width * _captureRect.Width;
        var clickY = (picY - picRect.Y) / picRect.Height * _captureRect.Height;
        return (clickX, clickY);
    }

    private async Task CheckInBigMapUi()
    {
        // 尝试打开地图失败后，先回到主界面后再次尝试打开地图
        if (!await TryToOpenBigMapUi())
        {
            await new ReturnMainUiTask().Start(ct);
            if (!await TryToOpenBigMapUi())
            {
                throw new RetryException("打开大地图失败，请检查按键绑定中「打开地图」按键设置是否和原神游戏中一致！");
            }
        }
    }

    /// <summary>
    /// 尝试打开地图界面
    /// </summary>
    private async Task<bool> TryToOpenBigMapUi()
    {
        // M 打开地图识别当前位置，中心点为当前位置
        var ra1 = CaptureToRectArea();
        if (!Bv.IsInBigMapUi(ra1))
        {
            Simulation.SendInput.SimulateAction(GIActions.OpenMap);
            await Delay(1000, ct);
            for (int i = 0; i < 3; i++)
            {
                ra1 = CaptureToRectArea();
                if (!Bv.IsInBigMapUi(ra1))
                {
                    await Delay(500, ct);
                }
                else
                {
                    return true;
                }
            }

            return false;
        }
        else
        {
            return true;
        }
    }


    public async Task<(double, double)> Tp(double tpX, double tpY, bool force = false)
    {
        for (var i = 0; i < 3; i++)
        {
            try
            {
                return await TpOnce(tpX, tpY, force: force);
            }
            catch (TpPointNotActivate e)
            {
                // 传送点未激活或不存在 按ESC回到大地图界面
                Simulation.SendInput.Keyboard.KeyPress(User32.VK.VK_ESCAPE);
                await Delay(300, ct);
                // throw; // 不抛出异常，继续重试
                Logger.LogWarning(e.Message + "  重试");
            }
            catch (Exception e)
            {
                Logger.LogError("传送失败，重试 {I} 次", i + 1);
                Logger.LogDebug(e, "传送失败，重试 {I} 次", i + 1);
            }
        }

        throw new InvalidOperationException("传送失败");
    }

    /// <summary>
    /// 移动地图到指定传送点位置
    /// 可能会移动不对，所以可以重试此方法
    /// </summary>
    /// <param name="x">目标x坐标</param>
    /// <param name="y">目标y坐标</param>
    /// <param name="finalZoomLevel">到达目标点的最小缩放等级，只在 MapZoomEnabled 为 True 生效</param>
    private async Task MoveMapTo(double x, double y, double finalZoomLevel = 2)
    {
        // 参数初始化
        double minZoomLevel = Math.Min(finalZoomLevel, _tpConfig.MinZoomLevel);
        double maxZoomLevel = _tpConfig.MaxZoomLevel;
        double currentZoomLevel = GetBigMapZoomLevel(CaptureToRectArea());
        int exceptionTimes = 0;
        Point2f mapCenterPoint;
        try
        {
            mapCenterPoint = GetPositionFromBigMap(); // 初始中心
        }
        catch (Exception e)
        {
            ++exceptionTimes;
            mapCenterPoint = new Point2f(0f, 0f);  // 其他恰当的初始值?
        }
        
        var (xOffset, yOffset) = (x - mapCenterPoint.X, y - mapCenterPoint.Y);
        double totalMoveMouseX = _tpConfig.MapScaleFactor * Math.Abs(xOffset) / currentZoomLevel;
        double totalMoveMouseY = _tpConfig.MapScaleFactor * Math.Abs(yOffset) / currentZoomLevel;
        double mouseDistance = Math.Sqrt(totalMoveMouseX * totalMoveMouseX + totalMoveMouseY * totalMoveMouseY);
        // 缩小地图到恰当的缩放
        if (_tpConfig.MapZoomEnabled) 
        {
            if (mouseDistance > _tpConfig.MapZoomOutDistance)
            {
                double targetZoomLevel = currentZoomLevel * mouseDistance / _tpConfig.MapZoomOutDistance;
                targetZoomLevel = Math.Min(targetZoomLevel, maxZoomLevel);
                await AdjustMapZoomLevel(currentZoomLevel, targetZoomLevel);
                double nextZoomLevel = GetBigMapZoomLevel(CaptureToRectArea());
                totalMoveMouseX *= currentZoomLevel / nextZoomLevel;
                totalMoveMouseY *= currentZoomLevel / nextZoomLevel;
                mouseDistance *= currentZoomLevel / nextZoomLevel;
                currentZoomLevel = nextZoomLevel;
            }
        }
        
        // 开始移动并放大地图
        for (var iteration = 0; iteration < _tpConfig.MaxIterations; iteration++)
        {
            if (_tpConfig.MapZoomEnabled)
            {
                if (mouseDistance < _tpConfig.MapZoomInDistance)
                {
                    double targetZoomLevel = currentZoomLevel * mouseDistance / _tpConfig.MapZoomInDistance;
                    targetZoomLevel = Math.Max(targetZoomLevel, minZoomLevel);
                    if (currentZoomLevel > minZoomLevel + _tpConfig.PrecisionThreshold)
                    {
                        await AdjustMapZoomLevel(currentZoomLevel, targetZoomLevel);
                        double nextZoomLevel = GetBigMapZoomLevel(CaptureToRectArea());
                        totalMoveMouseX *= currentZoomLevel / nextZoomLevel;
                        totalMoveMouseY *= currentZoomLevel / nextZoomLevel;
                        mouseDistance *= currentZoomLevel / nextZoomLevel;
                        currentZoomLevel = nextZoomLevel;
                    }
                }
            }

            // 非常接近目标点，不再进一步调整
            if (mouseDistance < _tpConfig.Tolerance)
            {
                Logger.LogInformation("移动 {I} 次鼠标后，已经接近目标点，不再移动地图。", iteration + 1);
                break;
            }

            int moveMouseX = (int)Math.Min(totalMoveMouseX, _tpConfig.MaxMouseMove * totalMoveMouseX / mouseDistance) * Math.Sign(xOffset);
            int moveMouseY = (int)Math.Min(totalMoveMouseY, _tpConfig.MaxMouseMove * totalMoveMouseY / mouseDistance) * Math.Sign(yOffset);
            double moveMouseLength = Math.Sqrt(moveMouseX * moveMouseX + moveMouseY * moveMouseY);
            int moveSteps = Math.Max((int)moveMouseLength / 10, 3); // 每次移动的步数最小为 3，避免除 0 错误
            
            await MouseMoveMap(moveMouseX, moveMouseY, moveSteps);
            try
            {
                exceptionTimes = 0;
                mapCenterPoint = GetPositionFromBigMap(); // 随循环更新的地图中心
            }
            catch (Exception)
            {
                if (++exceptionTimes > 2)
                {
                    throw new Exception("多次中心点识别失败，重新传送");
                }
                Logger.LogWarning("中心点识别失败，预测移动的距离");
                mapCenterPoint += new Point2f((float)(moveMouseX * currentZoomLevel / _tpConfig.MapScaleFactor),
                    (float)(moveMouseY * currentZoomLevel / _tpConfig.MapScaleFactor));
            }
            (xOffset, yOffset) = (x - mapCenterPoint.X, y - mapCenterPoint.Y);
            totalMoveMouseX = _tpConfig.MapScaleFactor * Math.Abs(xOffset) / currentZoomLevel;
            totalMoveMouseY = _tpConfig.MapScaleFactor * Math.Abs(yOffset) / currentZoomLevel;
            mouseDistance = Math.Sqrt(totalMoveMouseX * totalMoveMouseX + totalMoveMouseY * totalMoveMouseY);
        }
    }

    /// <summary>
    /// 点击并移动鼠标
    /// </summary>
    /// <param name="x1">鼠标初始位置x</param>
    /// <param name="y1">鼠标初始位置y</param>
    /// <param name="x2">鼠标移动后位置x</param> 
    /// <param name="y2">鼠标移动后位置y</param>
    public async Task MouseClickAndMove(int x1, int y1, int x2, int y2)
    {
        GlobalMethod.MoveMouseTo(x1, y1);
        await Delay(50, ct);
        GlobalMethod.LeftButtonDown();
        await Delay(50, ct);
        GlobalMethod.MoveMouseTo(x2, y2);
        await Delay(50, ct);
        GlobalMethod.LeftButtonUp();
        await Delay(50, ct);
    }

    /// <summary>
    /// 调整地图缩放级别以加速移动
    /// </summary>
    /// <param name="zoomIn">是否放大地图</param>
   [Obsolete]
    private async Task AdjustMapZoomLevel(bool zoomIn)
    {
        if (zoomIn)
        {
            GameCaptureRegion.GameRegionClick((rect, scale) => (_tpConfig.ZoomButtonX * scale, _zoomInButtonY * scale));
        }
        else
        {
            GameCaptureRegion.GameRegionClick((rect, scale) => (_tpConfig.ZoomButtonX * scale, _zoomOutButtonY * scale));
        }

        await Delay(100, ct);
    }


    /// <summary>
    /// 调整地图的缩放等级（整数缩放级别）。
    /// </summary>
    /// <param name="zoomLevel">目标等级：1-6。整数。随着数字变大地图越小，细节越少。</param>
    [Obsolete]
    public async Task AdjustMapZoomLevel(int zoomLevel)
    {
        for (int i = 0; i < 5; i++)
        {
            await AdjustMapZoomLevel(false);
        }

        await Delay(200, ct);
        for (int i = 0; i < 6 - zoomLevel; i++)
        {
            await AdjustMapZoomLevel(true);
        }
    }

    /// <summary>
    /// 调整地图的缩放级别（支持浮点数精度）。
    /// </summary>
    /// <param name="zoomLevel">当前缩放等级：1.0-6.0，浮点数。</param>
    /// <param name="targetZoomLevel">目标缩放等级：1.0-6.0，浮点数。</param>
    private async Task AdjustMapZoomLevel(double zoomLevel, double targetZoomLevel)
    {
        // Logger.LogInformation("调整地图缩放等级：{zoomLevel:0.000} -> {targetZoomLevel:0.000}", zoomLevel, targetZoomLevel);
        int initialY = (int)(_tpConfig.ZoomStartY + (_tpConfig.ZoomEndY - _tpConfig.ZoomStartY) * (zoomLevel - 1) / 5d);
        int targetY = (int)(_tpConfig.ZoomStartY + (_tpConfig.ZoomEndY - _tpConfig.ZoomStartY) * (targetZoomLevel - 1) / 5d);
        await MouseClickAndMove(_tpConfig.ZoomButtonX, initialY, _tpConfig.ZoomButtonX, targetY);
        await Delay(100, ct);
    }

    private async Task MouseMoveMap(int pixelDeltaX, int pixelDeltaY, int steps = 10)
    {
        // 确保不影响总移动距离
        int totalX = 0;
        int totalY = 0;
        // 梯形缩放因子
        double scaleFactor = 0.75;
        // 计算每一步的位移，从steps/2逐渐减小到0
        int[] stepX = new int[steps];
        int[] stepY = new int[steps];
        for (int i = 0; i < steps; i++)
        {
            double factor = ((double)(steps - Math.Max(i, steps / 2)) / (steps / 2)) / scaleFactor;
            stepX[i] = (int)(pixelDeltaX * factor / steps);
            stepY[i] = (int)(pixelDeltaY * factor / steps);
            totalX += stepX[i];
            totalY += stepY[i];
        }

        // 均匀分配多余的部分到前半段
        int remainingX = (pixelDeltaX - totalX);
        int remainingY = (pixelDeltaY - totalY);
        for (int i = 0; i < steps / 2 + 1; i++)
        {
            stepX[i] += remainingX / (steps / 2 + 1) + ((remainingX % (steps / 2 + 1) > i) ? 0 : 1);
            stepY[i] += remainingY / (steps / 2 + 1) + ((remainingX % (steps / 2 + 1) > i) ? 0 : 1);
        }

        // 随机起点以避免地图移动无效
        GameCaptureRegion.GameRegionMove((rect, _) =>
            (rect.Width / 2d + Random.Shared.Next(-rect.Width / 6, rect.Width / 6),
                rect.Height / 2d + Random.Shared.Next(-rect.Height / 6, rect.Height / 6)));

        Simulation.SendInput.Mouse.LeftButtonDown();
        for (var i = 0; i < steps; i++)
        {
            Simulation.SendInput.Mouse.MoveMouseBy(stepX[i], stepY[i]);
            await Delay(_tpConfig.StepIntervalMilliseconds, ct);
        }

        Simulation.SendInput.Mouse.LeftButtonUp();
    }

    public Point2f GetPositionFromBigMap()
    {
        return GetBigMapCenterPoint();
    }

    public Point2f? GetPositionFromBigMapNullable()
    {
        try
        {
            return GetBigMapCenterPoint();
        }
        catch
        {
            return null;
        }
    }

    public Rect GetBigMapRect()
    {
        var rect = new Rect();
        NewRetry.Do(() =>
        {
            // 判断是否在地图界面
            using var ra = CaptureToRectArea();
            using var mapScaleButtonRa = ra.Find(QuickTeleportAssets.Instance.MapScaleButtonRo);
            if (mapScaleButtonRa.IsExist())
            {
                rect = BigMap.Instance.GetBigMapRectByFeatureMatch(ra.SrcGreyMat);
                if (rect == Rect.Empty)
                {
                    // 滚轮调整后再次识别
                    Simulation.SendInput.Mouse.VerticalScroll(2);
                    Sleep(500);
                    throw new RetryException("识别大地图位置失败");
                }
            }
            else
            {
                throw new RetryException("当前不在地图界面");
            }
        }, TimeSpan.FromMilliseconds(500), 5);

        if (rect == Rect.Empty)
        {
            throw new InvalidOperationException("多次重试后，识别大地图位置失败");
        }

        Debug.WriteLine("识别大地图在全地图位置矩形：" + rect);
        const int s = BigMap.ScaleTo2048; // 相对1024做4倍缩放
        return MapCoordinate.Main2048ToGame(new Rect(rect.X * s, rect.Y * s, rect.Width * s, rect.Height * s));
    }

    public Point2f GetBigMapCenterPoint()
    {
        // 判断是否在地图界面
        using var ra = CaptureToRectArea();
        using var mapScaleButtonRa = ra.Find(QuickTeleportAssets.Instance.MapScaleButtonRo);
        if (mapScaleButtonRa.IsExist())
        {
            var p = BigMap.Instance.GetBigMapPositionByFeatureMatch(ra.SrcGreyMat);
            if (p.IsEmpty())
            {
                throw new InvalidOperationException("识别大地图位置失败");
            }

            Debug.WriteLine("识别大地图在全地图位置：" + p);
            return MapCoordinate.Main2048ToGame(new Point2f(BigMap.ScaleTo2048 * p.X, BigMap.ScaleTo2048 * p.Y));
        }
        else
        {
            throw new InvalidOperationException("当前不在地图界面");
        }
    }

    /// <summary>
    /// 获取最近的传送点位置和所处区域
    /// </summary>
    /// <param name="x"></param>
    /// <param name="y"></param>
    /// <returns></returns>
    [Obsolete]
    public (double x, double y, string? country) GetRecentlyTpPoint(double x, double y)
    {
        double recentX = 0;
        double recentY = 0;
        string? country = "";
        var minDistance = double.MaxValue;
        foreach (var tpPosition in MapLazyAssets.Instance.TpPositions)
        {
            var distance = Math.Sqrt(Math.Pow(tpPosition.X - x, 2) + Math.Pow(tpPosition.Y - y, 2));
            if (distance < minDistance)
            {
                minDistance = distance;
                recentX = tpPosition.X;
                recentY = tpPosition.Y;
                country = tpPosition.Country;
            }
        }

        return (recentX, recentY, country);
    }

    /// <summary>
    /// 获取最接近的N个传送点坐标和所处区域
    /// </summary>
    /// <param name="x"></param>
    /// <param name="y"></param>
    /// <param name="n">获取最近的 n 个传送点</param>
    /// <returns></returns>
    public List<GiTpPosition> GetNearestNTpPoints(double x, double y, int n = 1)
    {
        // 检查 n 的合法性
        if (n < 1)
        {
            throw new ArgumentException("The value of n must be greater than or equal to 1.", nameof(n));
        }

        // 按距离排序并选择前 n 个点
        return MapLazyAssets.Instance.TpPositions
            .OrderBy(tp => Math.Pow(tp.X - x, 2) + Math.Pow(tp.Y - y, 2))
            .Take(n)
            .ToList();
    }

    public async Task<bool> SwitchRecentlyCountryMap(double x, double y, string? forceCountry = null)
    {
        // 可能是地下地图，切换到地上地图
        using var ra2 = CaptureToRectArea();
        if (Bv.BigMapIsUnderground(ra2))
        {
            ra2.Find(_assets.MapUndergroundToGroundButtonRo).Click();
            await Delay(200, ct);
        }

        // 识别当前位置
        var minDistance = double.MaxValue;
        var bigMapCenterPointNullable = GetPositionFromBigMapNullable();

        if (bigMapCenterPointNullable != null)
        {
            var bigMapCenterPoint = bigMapCenterPointNullable.Value;
            Logger.LogDebug("识别当前大地图位置：{Pos}", bigMapCenterPoint);
            minDistance = Math.Sqrt(Math.Pow(bigMapCenterPoint.X - x, 2) + Math.Pow(bigMapCenterPoint.Y - y, 2));
            if (minDistance < 50)
            {
                // 点位很近的情况下不切换
                return false;
            }
        }

        string minCountry = "当前位置";
        foreach (var (country, position) in MapLazyAssets.Instance.CountryPositions)
        {
            var distance = Math.Sqrt(Math.Pow(position[0] - x, 2) + Math.Pow(position[1] - y, 2));
            if (distance < minDistance)
            {
                minDistance = distance;
                minCountry = country;
            }
        }

        Logger.LogDebug("离目标传送点最近的区域是：{Country}", minCountry);
        if (minCountry != "当前位置")
        {
            if (forceCountry != null)
            {
                minCountry = forceCountry;
            }

            GameCaptureRegion.GameRegionClick((rect, scale) => (rect.Width - 160 * scale, rect.Height - 60 * scale));
            await Delay(300, ct);
            var ra = CaptureToRectArea();
            var list = ra.FindMulti(new RecognitionObject
            {
                RecognitionType = RecognitionTypes.Ocr,
                RegionOfInterest = new Rect(ra.Width / 2, 0, ra.Width / 2, ra.Height)
            });
            list.FirstOrDefault(r => r.Text.Length == minCountry.Length && !r.Text.Contains("委托") && r.Text.Contains(minCountry))?.Click();
            Logger.LogInformation("切换到区域：{Country}", minCountry);
            await Delay(500, ct);
            return true;
        }

        return false;
    }


    public async Task Tp(string name)
    {
        // 通过大地图传送到指定传送点
        await Delay(500, ct);
    }

    public async Task TpByF1(string name)
    {
        // 传送到指定传送点
        await Delay(500, ct);
    }

    public async Task ClickTpPoint(ImageRegion imageRegion)
    {
        // 1.判断是否在地图界面
        if (Bv.IsInBigMapUi(imageRegion))
        {
            // 2. 判断是否已经点出传送按钮
            var hasTeleportButton = CheckTeleportButton(imageRegion);
            if (!hasTeleportButton)
            {
                // 3. 没点出传送按钮，且不存在外部地图关闭按钮
                // 说明只有两种可能，a. 点出来的是未激活传送点或者标点 b. 选择传送点选项列表
                var mapCloseRa1 = imageRegion.Find(_assets.MapCloseButtonRo);
                if (!mapCloseRa1.IsEmpty())
                {
                    throw new TpPointNotActivate("传送点未激活或不存在");
                }
                else
                {
                    // 3. 循环判断选项列表是否有传送点(未激活点位也在里面)
                    var hasMapChooseIcon = CheckMapChooseIcon(imageRegion);
                    if (hasMapChooseIcon)
                    {
                        var time = TaskContext.Instance().Config.QuickTeleportConfig.WaitTeleportPanelDelay;
                        time = time < 300 ? 300 : time;
                        await Delay(time, ct);
                        if (!CheckTeleportButton(CaptureToRectArea()))
                        {
                            // 没传送确认图标说明点开的是未激活传送锚点
                            throw new TpPointNotActivate("传送点未激活或不存在");
                        }
                    }
                    else
                    {
                        // 没有传送点说明不是传送点
                        throw new TpPointNotActivate("传送点未激活或不存在");
                    }
                }
            }
        }
    }

    private bool CheckTeleportButton(ImageRegion imageRegion)
    {
        var hasTeleportButton = false;
        imageRegion.Find(_assets.TeleportButtonRo, ra =>
        {
            ra.Click();
            hasTeleportButton = true;
        });
        return hasTeleportButton;
    }

    /// <summary>
    /// 全匹配一遍并进行文字识别
    /// 60ms ~200ms
    /// </summary>
    /// <param name="imageRegion"></param>
    /// <returns></returns>
    private bool CheckMapChooseIcon(ImageRegion imageRegion)
    {
        var hasMapChooseIcon = false;

        // 全匹配一遍
        var rResultList = MatchTemplateHelper.MatchMultiPicForOnePic(imageRegion.SrcGreyMat[_assets.MapChooseIconRoi], _assets.MapChooseIconGreyMatList);
        // 按高度排序
        if (rResultList.Count > 0)
        {
            rResultList = [.. rResultList.OrderBy(x => x.Y)];
            // 点击最高的
            foreach (var iconRect in rResultList)
            {
                // 200宽度的文字区域
                using var ra = imageRegion.DeriveCrop(_assets.MapChooseIconRoi.X + iconRect.X + iconRect.Width, _assets.MapChooseIconRoi.Y + iconRect.Y - 8, 200, iconRect.Height + 16);
                using var textRegion = ra.Find(new RecognitionObject
                {
                    // RecognitionType = RecognitionTypes.Ocr,
                    RecognitionType = RecognitionTypes.ColorRangeAndOcr,
                    LowerColor = new Scalar(249, 249, 249), // 只取白色文字
                    UpperColor = new Scalar(255, 255, 255),
                });
                if (string.IsNullOrEmpty(textRegion.Text) || textRegion.Text.Length == 1)
                {
                    continue;
                }

                Logger.LogInformation("传送：点击 {Option}", textRegion.Text.Replace(">", ""));
                var time = TaskContext.Instance().Config.QuickTeleportConfig.TeleportListClickDelay;
                time = time < 500 ? 500 : time;
                Thread.Sleep(time);
                ra.Click();
                hasMapChooseIcon = true;
                break;
            }
        }

        return hasMapChooseIcon;
    }

    /// <summary>
    /// 给定的映射关系可以表示成 (x, y) 对的形式，其中 x 是输入值，y 是输出值
    ///    1 - 1
    ///  0.8 - 2
    ///  0.6 - 3
    ///  0.4 - 4
    ///  0.2 - 5
    ///    0 - 6
    /// y=−5x+6
    /// </summary>
    /// <param name="region"></param>
    /// <returns></returns>
    private double GetBigMapZoomLevel(ImageRegion region)
    {
        var s = Bv.GetBigMapScale(region);
        // 1~6 的缩放等级
        return (-5 * s) + 6;
    }
}