using System;
using System.Collections.Generic;
using System.ComponentModel;

namespace Kean.Tools.RgvSimulatorGui
{
    /// <summary>
    /// 提升机寄存器地址（智世提升机协议 V2.3）：
    /// WCS→PLC 写在 46088 起，PLC→WCS 反馈在 46138 起；Uint32 低位字在前。
    /// </summary>
    internal static class HoistReg
    {
        // WCS → PLC（写区）
        public const int W_LifeCount = 46088, W_LifterCode = 46089, W_TaskCode = 46090, W_TaskType = 46092,
            W_Direction = 46093, W_Layer = 46094, W_TaskUpdate = 46095, W_StepType = 46096, W_StepUpdate = 46097,
            W_LongLevel = 46098, W_SysStop = 46099, W_SysRest = 46100, W_TaskInit = 46101;

        // PLC → WCS（反馈区）
        public const int F_LifeCount = 46138, F_LifterCode = 46139, F_Model = 46140, F_WarnType = 46141,
            F_Cargo = 46142, F_Vehicle = 46143, F_TaskCode = 46144, F_LayerCount = 46146, F_CurrentLayer = 46147,
            F_PlatformPos = 46148, F_ChainRun = 46149, F_Ready = 46150, F_SysStatus = 46151, F_UpdateCode = 46152,
            F_TaskType = 46154, F_TaskStatus = 46155, F_StepType = 46156, F_TaskUpdate = 46157, F_StepUpdate = 46158,
            F_WarnCode = 46159;
    }

    /// <summary>
    /// 提升机"PLC 行为"：状态机 + 每周期把 22 个反馈寄存器整块写下去。
    /// 操作面（UI）只改这里的字段，寄存器由 <see cref="Tick"/> 统一刷新，避免两处抢写。
    /// </summary>
    internal sealed class HoistBehavior
    {
        private const int MmPerTick = 300;          // 每周期平台移动量（250 ms 一轮 ≈ 1.2 m/s）
        private readonly ModbusTcpSlave _slave;
        private bool _taskAckPending;      // 已把"任务已读(2)"写给 WCS，下一轮清 0
        private bool _completionPending;   // 已把"任务完成(1)"写进反馈，等 WCS 写 2 确认后清 0

        public HoistBehavior(ModbusTcpSlave slave, int lifterCode, int layerCount, int currentLayer)
        {
            _slave = slave;
            LifterCode = lifterCode;
            LayerCount = Math.Max(1, layerCount);
            CurrentLayer = Math.Clamp(currentLayer, 1, LayerCount);
            TargetLayer = CurrentLayer;
            PlatformPos = (CurrentLayer - 1) * 3000;
            slave[HoistReg.W_LifterCode] = (ushort)lifterCode;
        }

        public int LifterCode { get; }
        public int Model { get; set; } = 1;            // 1 WCS 模式
        public int LayerCount { get; set; } = 3;
        public int CurrentLayer { get; private set; }
        public int TargetLayer { get; private set; }
        public int PlatformPos { get; private set; }
        public int ChainRun { get; private set; } = 3;  // 1 正转 2 反转 3 静止
        public int Cargo { get; set; } = 1;             // 1 无托盘 2 有托盘
        public int Vehicle { get; set; } = 1;           // 1 无车 2 有车
        public int WarnType { get; set; }
        public int WarnCode { get; set; }
        public int SysStatus { get; private set; } = 1; // 1 正常/执行 2 暂停 3 急停
        public bool LifeCountEnabled { get; set; } = true;
        public bool Busy { get; private set; }
        public uint TaskCode { get; private set; }
        public int TaskType { get; private set; }
        public int LastStepType { get; private set; }
        public int LifeCount { get; private set; }
        public int Paused { get; private set; }          // 1 = 收到暂停

        /// <summary>被输送线 PLC 呼叫占用（搬货交接进行中，不是 WCS 任务）：期间不接 WCS 任务、反馈非就绪</summary>
        public bool Held { get; private set; }
        private bool _localMove;                         // 当前升降是输送线呼叫的，到层后不写 WCS 的完成握手

        /// <summary>WCS 换层任务（2–5）到层后在等的交互完成：1 等上料完成（车已进入）、2 等下料完成（车已离开）、0 不在等。
        /// 协议：到层只报上料/下料就绪，任务保持执行（ready=2），WCS 写完成交互后才报任务完成</summary>
        public int AwaitingStep { get; private set; }

        /// <summary>平台正在升降</summary>
        public bool Moving => Busy && CurrentLayer != TargetLayer;

        /// <summary>交接进度（界面显示用），由 HoistTransferLink 维护</summary>
        public string TransferText { get; set; } = "—";

        /// <summary>就绪：model=1、无报警、不在搬运、未被输送线占用、有层位（协议表 1-3 的口径）</summary>
        public int Ready => Model == 1 && WarnType == 0 && !Busy && !Held && CurrentLayer != 0 ? 1 : 2;

        /// <summary>输送线 PLC 呼叫提升机：占用并在反馈区显示这次搬运的任务号、任务类型（6 输送入 / 7 输送出）</summary>
        public void BeginLocal(uint taskCode, int taskType)
        {
            Held = true;
            TaskCode = taskCode;
            TaskType = taskType;
        }

        /// <summary>输送线呼叫的升降：到层后只停下，不写 WCS 的完成握手</summary>
        public void MoveLocal(int layer)
        {
            if (layer < 1 || layer > LayerCount || layer == CurrentLayer)
            {
                return;
            }
            TargetLayer = layer;
            Busy = true;
            _localMove = true;
        }

        /// <summary>输送线呼叫的搬运结束：反馈区写完成任务号与状态（输送线那边自己确认，不置 taskUpdate），释放占用</summary>
        public void EndLocal()
        {
            _slave[HoistReg.F_UpdateCode] = (ushort)(TaskCode & 0xFFFF);
            _slave[HoistReg.F_UpdateCode + 1] = (ushort)(TaskCode >> 16);
            _slave[HoistReg.F_TaskType] = (ushort)TaskType;
            _slave[HoistReg.F_TaskStatus] = 1;
            Held = false;
            TransferText = "—";
        }

        /// <summary>中止输送线呼叫（停止提升机、清交接时用）：只释放占用，不写完成</summary>
        public void AbortLocal()
        {
            Held = false;
            if (_localMove)
            {
                _localMove = false;
                Busy = false;
                TargetLayer = CurrentLayer;
                ChainRun = 3;
            }
            TransferText = "—";
        }

        /// <summary>每个 UI 周期调用：跳生命计数、处理 WCS 写区、推进平台、整块刷新反馈区</summary>
        public void Tick()
        {
            if (LifeCountEnabled)
            {
                LifeCount = (LifeCount + 1) & 0xFFFF;
                _slave[HoistReg.F_LifeCount] = (ushort)LifeCount;
            }

            HandleWcsWrites();

            // 上一轮给 WCS 的"已读"回应只保留一个周期
            if (_taskAckPending)
            {
                _slave[HoistReg.W_TaskUpdate] = 0;
                _taskAckPending = false;
            }

            // 暂停或报警时平台停住（报警复位后继续）
            if (Busy && Paused == 0 && WarnType == 0 && CurrentLayer != TargetLayer)
            {
                int target = (TargetLayer - 1) * 3000;
                if (PlatformPos < target)
                {
                    PlatformPos = Math.Min(target, PlatformPos + MmPerTick);
                    ChainRun = 1;
                }
                else if (PlatformPos > target)
                {
                    PlatformPos = Math.Max(target, PlatformPos - MmPerTick);
                    ChainRun = 2;
                }
                if (PlatformPos == target)
                {
                    CurrentLayer = TargetLayer;
                    ChainRun = 3;
                    if (_localMove)
                    {
                        _localMove = false;          // 输送线呼叫的升降：到层即可，交接由 HoistTransferLink 接着做
                        Busy = false;
                    }
                    else
                    {
                        ArriveWcsTask();
                    }
                }
            }

            WriteFeedback();
        }

        /// <summary>处理 WCS 写区：taskUpdate / stepUpdate 握手、暂停、复位</summary>
        private void HandleWcsWrites()
        {
            // 复位：清报警并回零（协议：PLC 复位一次，同时把 1 重置掉）
            if (_slave[HoistReg.W_SysRest] == 1)
            {
                WarnType = 0;
                WarnCode = 0;
                Paused = 0;
                SysStatus = 1;
                _slave[HoistReg.W_SysRest] = 0;
            }

            // 暂停 / 启动
            int stop = _slave[HoistReg.W_SysStop];
            if (stop == 1)
            {
                Paused = 1;
                SysStatus = 2;
            }
            else if (stop == 2)
            {
                Paused = 0;
                SysStatus = 1;
            }

            // 新任务：WCS 写 taskUpdate=1（被输送线占用时不接，等交接结束、就绪后再接）
            if (_slave[HoistReg.W_TaskUpdate] == 1 && !Busy && !Held)
            {
                TaskCode = (uint)(_slave[HoistReg.W_TaskCode] | (_slave[HoistReg.W_TaskCode + 1] << 16));
                TaskType = _slave[HoistReg.W_TaskType];
                int layer = _slave[HoistReg.W_Layer];
                _slave[HoistReg.W_TaskUpdate] = 2;      // 已读任务
                _taskAckPending = true;
                if (layer >= 1 && layer <= LayerCount && layer != CurrentLayer)
                {
                    TargetLayer = layer;
                    Busy = true;
                    SysStatus = 1;
                }
                else
                {
                    ArriveWcsTask();
                }
            }

            // 换层任务等交互完成：WCS 写 stepType=1/2（上料/下料完成）+ stepUpdate=1 → PLC 置 stepUpdate=2（已读），任务完成
            if (AwaitingStep != 0 && _slave[HoistReg.W_StepUpdate] == 1 && _slave[HoistReg.W_StepType] == AwaitingStep)
            {
                _slave[HoistReg.W_StepUpdate] = 2;
                AwaitingStep = 0;
                TransferText = "—";
                FinishTask();
            }

            // 任务初始化：放弃当前 WCS 任务（人工处理异常用），回到就绪
            if (_slave[HoistReg.W_TaskInit] == 1)
            {
                _slave[HoistReg.W_TaskInit] = 0;
                if (!_localMove)
                {
                    Busy = false;
                    TargetLayer = CurrentLayer;
                    ChainRun = 3;
                }
                AwaitingStep = 0;
                TransferText = "—";
                _slave[HoistReg.F_StepType] = 0;
                _slave[HoistReg.F_StepUpdate] = 0;
            }

            // WCS 已读交互（stepUpdate=2）→ 清掉交互位
            if (_slave[HoistReg.W_StepUpdate] == 2)
            {
                _slave[HoistReg.W_StepUpdate] = 0;
                _slave[HoistReg.W_StepType] = 0;
                _slave[HoistReg.F_StepUpdate] = 0;
                _slave[HoistReg.F_StepType] = 0;
            }
        }

        /// <summary>
        /// WCS 任务到层（或无需移动）：
        /// 换层 2、3（车进入）→ 报上料就绪，4、5（车出）→ 报下料就绪，任务保持执行（ready=2），等 WCS 写对应的交互完成；
        /// 其他类型（1 升降、6/7 输送）到层即完成
        /// </summary>
        private void ArriveWcsTask()
        {
            ChainRun = 3;
            int step = TaskType switch { 2 or 3 => 1, 4 or 5 => 2, _ => 0 };
            if (step == 0)
            {
                FinishTask();
                return;
            }
            Busy = true;                                // 等车进出期间不接别的活（含输送线呼叫）
            TargetLayer = CurrentLayer;
            AwaitingStep = step;
            TransferText = step == 1 ? $"换层任务 {TaskCode}：等车进入（上料完成）" : $"换层任务 {TaskCode}：等车离开（下料完成）";
            LastStepType = step;
            _slave[HoistReg.F_StepType] = (ushort)step; // 1 上料就绪 / 2 下料就绪
            _slave[HoistReg.F_StepUpdate] = 1;          // PLC 写 1 表示交互有更新，等 WCS 写 2
        }

        /// <summary>任务完成：按协议写完成反馈</summary>
        private void FinishTask()
        {
            Busy = false;
            ChainRun = 3;
            _slave[HoistReg.F_UpdateCode] = (ushort)(TaskCode & 0xFFFF);
            _slave[HoistReg.F_UpdateCode + 1] = (ushort)(TaskCode >> 16);
            _slave[HoistReg.F_TaskType] = (ushort)TaskType;
            _slave[HoistReg.F_TaskStatus] = 1;          // 1 任务完成
            _slave[HoistReg.F_TaskUpdate] = 1;          // PLC 写 1 表示任务更新
            _completionPending = true;                  // 一直保持到 WCS 写 2 确认

            // 输送交互（6 输送入 / 7 输送出）沿用旧做法：完成时报上料/下料就绪；换层 2–5 的交互在到层时已报过
            LastStepType = TaskType switch { 6 => 1, 7 => 2, _ => 0 };
            if (LastStepType != 0)
            {
                _slave[HoistReg.F_StepType] = (ushort)LastStepType;
                _slave[HoistReg.F_StepUpdate] = 1;
            }
        }

        /// <summary>整块刷新反馈区（除了任务更新位，它在完成/被读时才变）</summary>
        private void WriteFeedback()
        {
            _slave[HoistReg.F_LifterCode] = (ushort)LifterCode;
            _slave[HoistReg.F_Model] = (ushort)Model;
            _slave[HoistReg.F_WarnType] = (ushort)WarnType;
            _slave[HoistReg.F_WarnCode] = (ushort)WarnCode;
            _slave[HoistReg.F_Cargo] = (ushort)Cargo;
            _slave[HoistReg.F_Vehicle] = (ushort)Vehicle;
            _slave[HoistReg.F_TaskCode] = (ushort)(TaskCode & 0xFFFF);
            _slave[HoistReg.F_TaskCode + 1] = (ushort)(TaskCode >> 16);
            _slave[HoistReg.F_LayerCount] = (ushort)LayerCount;
            _slave[HoistReg.F_CurrentLayer] = (ushort)CurrentLayer;
            _slave[HoistReg.F_PlatformPos] = (ushort)Math.Clamp(PlatformPos, 0, 65535);
            _slave[HoistReg.F_ChainRun] = (ushort)ChainRun;
            _slave[HoistReg.F_Ready] = (ushort)Ready;
            _slave[HoistReg.F_SysStatus] = (ushort)SysStatus;
        }

        /// <summary>WCS 已读任务更新（taskUpdate=2）时清掉我们写的 1</summary>
        public void AckTaskUpdateIfRead()
        {
            // 协议写法是 WCS 把反馈区 taskUpdate（46157）写成 2；也兼容写在写区（46095）的旧做法
            if (_completionPending && (_slave[HoistReg.F_TaskUpdate] == 2 || _slave[HoistReg.W_TaskUpdate] == 2))
            {
                _slave[HoistReg.F_TaskUpdate] = 0;
                _slave[HoistReg.W_TaskUpdate] = 0;
                _completionPending = false;
            }
        }

        /// <summary>手动置层（UI 用）：直接跳到该层的平台高度</summary>
        public void ForceLayer(int layer)
        {
            CurrentLayer = Math.Clamp(layer, 1, LayerCount);
            TargetLayer = CurrentLayer;
            PlatformPos = (CurrentLayer - 1) * 3000;
            ChainRun = 3;
            Busy = false;
            AwaitingStep = 0;                            // 手动置层视为放弃当前换层任务
        }
    }

    /// <summary>下拉框选项：寄存器值 + 显示文字</summary>
    public sealed record CodeOption(int Value, string Text);

    /// <summary>提升机表格下拉框的选项（XAML 用 x:Static 引用，所以单独做成 public）</summary>
    public static class HoistOptions
    {
        /// <summary>model 寄存器：1 WCS 2 手动 3 检修</summary>
        public static IReadOnlyList<CodeOption> ModelOptions { get; } = new[]
        {
            new CodeOption(1, "1 WCS"), new CodeOption(2, "2 手动"), new CodeOption(3, "3 检修")
        };

        /// <summary>cargo / vehicle 寄存器：1 无 2 有</summary>
        public static IReadOnlyList<CodeOption> PresenceOptions { get; } = new[]
        {
            new CodeOption(1, "1 无"), new CodeOption(2, "2 有")
        };
    }

    /// <summary>提升机一行的视图模型：属性 setter 直接改行为状态，Tick 后由 Refresh 回显</summary>
    internal sealed class HoistVm : INotifyPropertyChanged
    {
        private readonly HoistBehavior _behavior;
        private int _layer, _cargo, _vehicle, _warnType, _warnCode, _model, _clients;

        public HoistVm(int device, int port, HoistBehavior behavior, ModbusTcpSlave slave, Action<string> log)
        {
            Device = device;
            Port = port;
            _behavior = behavior;
            Slave = slave;
            Log = log;
            _layer = behavior.CurrentLayer;
            _model = behavior.Model;
            _cargo = behavior.Cargo;
            _vehicle = behavior.Vehicle;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public int Device { get; }
        public int Port { get; }
        public ModbusTcpSlave Slave { get; }
        public Action<string> Log { get; }
        public HoistBehavior Behavior => _behavior;
        public int Clients => _clients;
        public bool IsConnected => _clients > 0;
        public string ConnectionText => _clients > 0 ? $"已连接 {_clients}" : "未连接";
        public int Ready => _behavior.Ready;
        public string ReadyText => _behavior.Ready == 1 ? "1 就绪" : "2 未就绪";
        public string SysStatusText => _behavior.SysStatus switch { 1 => "1 正常", 2 => "2 暂停", 3 => "3 急停", var v => v.ToString() };
        public string ChainText => _behavior.ChainRun switch { 1 => "1 正转", 2 => "2 反转", 3 => "3 静止", var v => v.ToString() };
        public int SysStatus => _behavior.SysStatus;
        public int ChainRun => _behavior.ChainRun;
        public int PlatformPos => _behavior.PlatformPos;
        public int LifeCount => _behavior.LifeCount;
        public uint CurrentTaskCode => _behavior.TaskCode;
        public int CurrentTaskType => _behavior.TaskType;
        public string TransferText => _behavior.TransferText;
        public int LayerCount => _behavior.LayerCount;

        public int Model
        {
            get => _model;
            set { if (_model == value) return; _model = value; _behavior.Model = value; Raise(nameof(Model)); Log($"提升机{Device} 模式 → {value}（1=WCS 2=手动 3=检修）"); }
        }

        public int Layer
        {
            get => _layer;
            set
            {
                if (_layer == value) return;
                if (value < 1 || value > _behavior.LayerCount) { Log($"提升机{Device}：层号要填 1-{_behavior.LayerCount}"); Raise(nameof(Layer)); return; }
                _layer = value;
                _behavior.ForceLayer(value);
                Raise(nameof(Layer));
                Log($"提升机{Device} 当前层 → {value}");
            }
        }

        public int Cargo
        {
            get => _cargo;
            set { if (_cargo == value) return; _cargo = value; _behavior.Cargo = value; Raise(nameof(Cargo)); Log($"提升机{Device} 托盘 → {value}（1 无 2 有）"); }
        }

        public int Vehicle
        {
            get => _vehicle;
            set { if (_vehicle == value) return; _vehicle = value; _behavior.Vehicle = value; Raise(nameof(Vehicle)); Log($"提升机{Device} 车状态 → {value}（1 无 2 有）"); }
        }

        public int WarnType
        {
            get => _warnType;
            set { if (_warnType == value) return; _warnType = value; _behavior.WarnType = value; Raise(nameof(WarnType)); Log($"提升机{Device} 报警类型 → {value}"); }
        }

        public int WarnCode
        {
            get => _warnCode;
            set { if (_warnCode == value) return; _warnCode = value; _behavior.WarnCode = value; Raise(nameof(WarnCode)); Log($"提升机{Device} 报警码 → {value}"); }
        }

        /// <summary>把行为里的状态读回来显示（只读回显）</summary>
        public void Refresh()
        {
            if (_layer != _behavior.CurrentLayer) { _layer = _behavior.CurrentLayer; Raise(nameof(Layer)); }
            Raise(nameof(Ready));
            Raise(nameof(ReadyText));
            Raise(nameof(SysStatus));
            Raise(nameof(SysStatusText));
            Raise(nameof(ChainRun));
            Raise(nameof(ChainText));
            Raise(nameof(PlatformPos));
            Raise(nameof(LifeCount));
            Raise(nameof(CurrentTaskCode));
            Raise(nameof(CurrentTaskType));
            Raise(nameof(TransferText));
            if (_model != _behavior.Model) { _model = _behavior.Model; Raise(nameof(Model)); }
            if (_warnType != _behavior.WarnType) { _warnType = _behavior.WarnType; Raise(nameof(WarnType)); }
            if (_warnCode != _behavior.WarnCode) { _warnCode = _behavior.WarnCode; Raise(nameof(WarnCode)); }
            if (_cargo != _behavior.Cargo) { _cargo = _behavior.Cargo; Raise(nameof(Cargo)); }
            if (_vehicle != _behavior.Vehicle) { _vehicle = _behavior.Vehicle; Raise(nameof(Vehicle)); }
            int clients = Slave.ClientCount;
            if (_clients != clients) { _clients = clients; Raise(nameof(Clients)); Raise(nameof(IsConnected)); Raise(nameof(ConnectionText)); }
        }

        private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
