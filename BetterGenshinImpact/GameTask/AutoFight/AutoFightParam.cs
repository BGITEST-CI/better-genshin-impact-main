﻿using BetterGenshinImpact.GameTask.Model;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BetterGenshinImpact.GameTask.AutoFight;

public class AutoFightParam : BaseTaskParam
{
    public string CombatStrategyContent { get; set; }
    public AutoFightParam(CancellationTokenSource cts, string content) : base(cts)
    {
        CombatStrategyContent = content;
    }
}