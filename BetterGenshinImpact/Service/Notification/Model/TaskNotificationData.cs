﻿using BetterGenshinImpact.Service.Notification.Converter;
using BetterGenshinImpact.Service.Notification.Model.Enum;
using System.Drawing;
using System.Text.Json.Serialization;
using BetterGenshinImpact.Service.Notification.Model.Base;

namespace BetterGenshinImpact.Service.Notification.Model;

public class TaskNotificationData : INotificationData
{
    public TaskDetails? Task { get; set; }
}
