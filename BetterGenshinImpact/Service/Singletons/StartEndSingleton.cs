﻿using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.Helpers.Device;
using BetterGenshinImpact.Model;
using BetterGenshinImpact.ViewModel.Pages;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.Service.Singletons;

public class StartEndSingleton: Singleton<StartEndSingleton>
{
    private Resolution? _resolution;
    
    public void OnStartup()
    {

    }
    
    public void OnMainWindowLoad()
    {
                
        TouchpadSoft.Instance.CheckAndRecordStatus();
        TouchpadSoft.Instance.DisableTouchpadWhenEnabledByHotKey();


        if (TaskContext.Instance().Config.CommonConfig.ChangeResolutionOnStart)
        {
            // 设置DPI
            SysDpi.Instance.SetDpi();
            
            Thread.Sleep(2000);
            
            ChangeResolution();

        }
        
        // 获取PC信息
        Task.Run(() =>
        {
            try
            {
                var json = GetPCInfo.GetJson();
                // 保存
                File.WriteAllText(Global.Absolute(@$"User/pc.json"), json);
            }
            catch (Exception e)
            {
                TaskControl.Logger.LogDebug("获取PC信息失败：" + e.Source + "\r\n--" + Environment.NewLine + e.StackTrace + "\r\n---" + Environment.NewLine + e.Message);
            }
        });


    }
    
    public void OnExit()
    {
        TouchpadSoft.Instance.RestoreTouchpadByHotKey();
        
        if (TaskContext.Instance().Config.CommonConfig.RestoreResolutionOnExit)
        {
            ResetResolution();
            Thread.Sleep(2000);
            SysDpi.Instance.ResetDpi();
        }

    }
    
    
    public void ChangeResolution()
    {
        _resolution = new Resolution();
        _resolution.ChangeResolution(1920, 1080);
    }

    public void ResetResolution()
    {
        _resolution?.ChangeResolution(_resolution.autoWidth, _resolution.autoHeight);
    }
}