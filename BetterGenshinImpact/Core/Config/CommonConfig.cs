﻿using CommunityToolkit.Mvvm.ComponentModel;
using System;

namespace BetterGenshinImpact.Core.Config;

/// <summary>
///     遮罩窗口配置
/// </summary>
[Serializable]
public partial class CommonConfig : ObservableObject
{
    /// <summary>
    ///     是否启用遮罩窗口
    /// </summary>
    [ObservableProperty]
    private bool _screenshotEnabled;

    /// <summary>
    ///     UID遮盖是否启用
    /// </summary>
    [ObservableProperty]
    private bool _screenshotUidCoverEnabled;

    /// <summary>
    ///     退出时最小化至托盘
    /// </summary>
    [ObservableProperty]
    private bool _exitToTray;
    
    
        
    [ObservableProperty]
    private bool _processCheckEnabled = true;
    
    [ObservableProperty]
    private string _ffmpegCommand = " -f gdigrab -framerate 60 -use_wallclock_as_timestamps 1 -i title=原神 -pix_fmt yuv420p  -c:v libx264 -preset ultrafast -f segment -segment_time 1800 -reset_timestamps 1 -strftime 1  ";
}
