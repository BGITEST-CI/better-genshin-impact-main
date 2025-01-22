﻿using CommunityToolkit.Mvvm.ComponentModel;
using System;

namespace BetterGenshinImpact.Service.Notification;

/// <summary>
///     Notification
/// </summary>
[Serializable]
public partial class NotificationConfig : ObservableObject
{
    /// <summary>
    ///
    /// </summary>
    [ObservableProperty]
    private bool _webhookEnabled;

    /// <summary>
    ///
    /// </summary>
    [ObservableProperty]
    private string _webhookEndpoint = string.Empty;

    /// <summary>
    /// 是否包含截图
    /// </summary>
    [ObservableProperty]
    private bool _includeScreenShot = false;
}
