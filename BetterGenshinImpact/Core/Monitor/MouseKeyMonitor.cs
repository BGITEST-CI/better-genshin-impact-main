﻿using BetterGenshinImpact.Core.Recorder;
using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.Model;
using Gma.System.MouseKeyHook;
using System;
using System.Diagnostics;
using System.Windows.Forms;
using Vanara.PInvoke;
using Timer = System.Timers.Timer;

namespace BetterGenshinImpact.Core.Monitor;

public class MouseKeyMonitor
{
    /// <summary>
    ///     长按F变F连发
    /// </summary>
    private readonly Timer _fTimer = new();

    //private readonly Random _random = new();

    /// <summary>
    ///     长按空格变空格连发
    /// </summary>
    private readonly Timer _spaceTimer = new();

    private DateTime _firstFKeyDownTime = DateTime.MaxValue;

    /// <summary>
    ///     DateTime.MaxValue 代表没有按下
    /// </summary>
    private DateTime _firstSpaceKeyDownTime = DateTime.MaxValue;

    private IKeyboardMouseEvents? _globalHook;
    private nint _hWnd;

    public void Subscribe(nint gameHandle)
    {
        _hWnd = gameHandle;
        // Note: for the application hook, use the Hook.AppEvents() instead
        _globalHook = Hook.GlobalEvents();

        _globalHook.KeyDownExt += GlobalHookKeyDown;
        _globalHook.KeyUpExt += GlobalHookKeyUp;
        _globalHook.MouseDownExt += GlobalHookMouseDownExt;
        _globalHook.MouseUpExt += GlobalHookMouseUpExt;
        _globalHook.MouseMoveExt += GlobalHookMouseMoveExt;
        _globalHook.MouseWheelExt += GlobalHookMouseWheelExt;
        //_globalHook.KeyPress += GlobalHookKeyPress;

        _firstSpaceKeyDownTime = DateTime.MaxValue;
        var si = TaskContext.Instance().Config.MacroConfig.SpaceFireInterval;
        _spaceTimer.Interval = si;
        _spaceTimer.Elapsed += (sender, args) => { Simulation.PostMessage(_hWnd).KeyPress(User32.VK.VK_SPACE); };

        var fi = TaskContext.Instance().Config.MacroConfig.FFireInterval;
        _fTimer.Interval = fi;
        _fTimer.Elapsed += (sender, args) => { Simulation.PostMessage(_hWnd).KeyPress(User32.VK.VK_F); };
    }

    private void GlobalHookKeyDown(object? sender, KeyEventArgsExt e)
    {
        // Debug.WriteLine(Kernel32.GetTickCount());
        // Debug.WriteLine(System.Environment.TickCount);
        // Debug.WriteLine("KeyDown: \t{0} \tIsKeyDown:{1} \tIsKeyUp:{2} \t Time:{3}", e.KeyCode, e.IsKeyDown, e.IsKeyUp, e.Timestamp);
        GlobalKeyMouseRecord.Instance.GlobalHookKeyDown(e);

        // 热键按下事件
        HotKeyDown(sender, e);

        if (e.KeyCode == Keys.Space)
        {
            if (_firstSpaceKeyDownTime == DateTime.MaxValue)
            {
                _firstSpaceKeyDownTime = DateTime.Now;
            }
            else
            {
                var timeSpan = DateTime.Now - _firstSpaceKeyDownTime;
                if (timeSpan.TotalMilliseconds > 300 && TaskContext.Instance().Config.MacroConfig.SpacePressHoldToContinuationEnabled)
                    if (!_spaceTimer.Enabled)
                        _spaceTimer.Start();
            }
        }
        else if (e.KeyCode == Keys.F)
        {
            if (_firstFKeyDownTime == DateTime.MaxValue)
            {
                _firstFKeyDownTime = DateTime.Now;
            }
            else
            {
                var timeSpan = DateTime.Now - _firstFKeyDownTime;
                if (timeSpan.TotalMilliseconds > 200 && TaskContext.Instance().Config.MacroConfig.FPressHoldToContinuationEnabled)
                    if (!_fTimer.Enabled)
                        _fTimer.Start();
            }
        }
    }

    private void GlobalHookKeyUp(object? sender, KeyEventArgsExt e)
    {
        // Debug.WriteLine("KeyUp: \t{0}", e.KeyCode);
        GlobalKeyMouseRecord.Instance.GlobalHookKeyUp(e);

        // 热键松开事件
        HotKeyUp(sender, e);

        if (e.KeyCode == Keys.Space)
        {
            if (_firstSpaceKeyDownTime != DateTime.MaxValue)
            {
                var timeSpan = DateTime.Now - _firstSpaceKeyDownTime;
                Debug.WriteLine($"Space按下时间：{timeSpan.TotalMilliseconds}ms");
                _firstSpaceKeyDownTime = DateTime.MaxValue;
                _spaceTimer.Stop();
            }
        }
        else if (e.KeyCode == Keys.F)
        {
            if (_firstFKeyDownTime != DateTime.MaxValue)
            {
                var timeSpan = DateTime.Now - _firstFKeyDownTime;
                Debug.WriteLine($"F按下时间：{timeSpan.TotalMilliseconds}ms");
                _firstFKeyDownTime = DateTime.MaxValue;
                _fTimer.Stop();
            }
        }
    }

    private void HotKeyDown(object? sender, KeyEventArgs e)
    {
        if (KeyboardHook.AllKeyboardHooks.TryGetValue(e.KeyCode, out var hook)) hook.KeyDown(sender, e);
    }

    private void HotKeyUp(object? sender, KeyEventArgs e)
    {
        if (KeyboardHook.AllKeyboardHooks.TryGetValue(e.KeyCode, out var hook)) hook.KeyUp(sender, e);
    }

    //private void GlobalHookKeyPress(object? sender, KeyPressEventArgs e)
    //{
    //    Debug.WriteLine("KeyPress: \t{0}", e.KeyChar);
    //}

    private void GlobalHookMouseDownExt(object? sender, MouseEventExtArgs e)
    {
        // Debug.WriteLine("MouseDown: {0}; \t Location: {1};\t System Timestamp: {2}", e.Button, e.Location, e.Timestamp);
        GlobalKeyMouseRecord.Instance.GlobalHookMouseDown(e);

        if (e.Button != MouseButtons.Left)
            if (MouseHook.AllMouseHooks.TryGetValue(e.Button, out var hook))
                hook.MouseDown(sender, e);
    }

    private void GlobalHookMouseUpExt(object? sender, MouseEventExtArgs e)
    {
        // Debug.WriteLine("MouseUp: {0}; \t Location: {1};\t System Timestamp: {2}", e.Button, e.Location, e.Timestamp);
        GlobalKeyMouseRecord.Instance.GlobalHookMouseUp(e);

        if (e.Button != MouseButtons.Left)
            if (MouseHook.AllMouseHooks.TryGetValue(e.Button, out var hook))
                hook.MouseUp(sender, e);
    }

    private void GlobalHookMouseMoveExt(object? sender, MouseEventExtArgs e)
    {
        // Debug.WriteLine("MouseMove: {0}; \t Location: {1};\t System Timestamp: {2}", e.Button, e.Location, e.Timestamp);
        GlobalKeyMouseRecord.Instance.GlobalHookMouseMoveTo(e);
    }
    
    private void GlobalHookMouseWheelExt(object? sender, MouseEventExtArgs e)
    {
        // Debug.WriteLine("MouseMove: {0}; \t Location: {1};\t Delta: {2};\t System Timestamp: {3}", e.Button, e.Location, e.Delta, e.Timestamp);
        GlobalKeyMouseRecord.Instance.GlobalHookMouseWheel(e);
    }

    public void Unsubscribe()
    {
        if (_globalHook != null)
        {
            _globalHook.KeyDownExt -= GlobalHookKeyDown;
            _globalHook.KeyUpExt -= GlobalHookKeyUp;
            _globalHook.MouseDownExt -= GlobalHookMouseDownExt;
            _globalHook.MouseUpExt -= GlobalHookMouseUpExt;
            _globalHook.MouseMoveExt -= GlobalHookMouseMoveExt;
            _globalHook.MouseWheelExt -= GlobalHookMouseWheelExt;
            //_globalHook.KeyPress -= GlobalHookKeyPress;
            _globalHook.Dispose();
        }
    }
}
