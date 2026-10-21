using System;

namespace Kean.Tools.RgvSimulatorGui
{
    /// <summary>
    /// 输送线 PLC 的行为：接 WCS 指令、带货往下游走、到终点报完成。每 250ms 由界面定时器对每台输送线调用一次 <see cref="Tick"/>。
    /// <para>规则（按现场 PLC 的做法）：</para>
    /// <para>1. 一条指令 = 起点 → 终点。货一节一节往前走，路过的节上报“运行 + 指令号”，货交给下一节后回空闲、清指令号；
    /// 只有终点那一节报完成，完成保持到 WCS 写应答后清 DB 区（MainWindow.HandleConveyorAck）。</para>
    /// <para>2. 只在空闲时接新指令：运行中、完成未应答、故障、被提升机预约接货时拒收（记日志）。本节没货时不空跑、不报完成，等货送来。</para>
    /// <para>3. 层间线和一楼输送线之间只能经提升机相通：路线经过提升机时交给 <see cref="HoistTransferLink"/>，
    /// 不按“下一节”直接交（表里默认按设备号串的“下一节”对这几节没有物理意义）。</para>
    /// <para>4. 下游有货、在运行、完成未清或被预约时，货停在本节端头等待。</para>
    /// <para>5. 状态“运行（1）”只表示输送线真的在转。停着等（等下一节、等提升机、等货送来、配置不对）一律上报“空闲（0）+ 有货位 + 保留指令号”，
    /// 期间不接新的 WCS 指令、不给“允许取货”；条件满足重新转起来时再报运行。</para>
    /// </summary>
    internal sealed class ConveyorFlow
    {
        private readonly Func<int, ConveyorDevice> _find;
        private readonly Func<int> _runTicks;
        private readonly Action<string> _log;

        /// <param name="find">按设备号找本机设备表里的输送线，没有返回 null</param>
        /// <param name="runTicks">一节输送线运行一次的周期数</param>
        /// <param name="log">日志</param>
        public ConveyorFlow(Func<int, ConveyorDevice> find, Func<int> runTicks, Action<string> log)
        {
            _find = find;
            _runTicks = runTicks;
            _log = log;
        }

        /// <summary>经提升机交接（输送线 PLC 呼叫提升机）；为 null 时只走「下一节」</summary>
        public HoistTransferLink Hoists { get; set; }

        public int RunTicks => _runTicks();

        /// <summary>
        /// 一个 250ms 周期：跑够运行时间就把货交给下游。返回 true 表示反馈值可能变了，要推给 OPC 订阅方。
        /// </summary>
        public bool Tick(ConveyorDevice device)
        {
            bool changed = false;
            if (device.CargoDirty)                       // 界面上改了有货，或交接改了状态
            {
                device.CargoDirty = false;
                changed = true;
            }
            if (device.State == 1)                       // 运行中
            {
                if (device.BusyTicks > 0)
                {
                    device.BusyTicks--;
                    if (device.BusyTicks > 0)
                    {
                        return changed;
                    }
                }
                changed |= TryHandOver(device);          // 货到端头
            }
            else if (device.State == 0 && device.WaitingDownstream)
            {
                changed |= TryHandOver(device);          // 停着等（上报空闲）：每轮再试
            }
            return changed;
        }

        /// <summary>
        /// WCS 下指令：只在空闲时接。返回 null 表示已开始运行，否则返回拒收原因（由调用方记日志）。
        /// </summary>
        public string Start(ConveyorDevice device, int type, int instruction, int destination)
        {
            string reason =
                device.State == 1 ? $"正在执行指令 {device.Instruction}" :
                device.State == 2 ? $"指令 {device.Instruction} 已完成、还没收到 WCS 应答" :
                device.State >= 30 ? $"故障 {device.State}" :
                device.Reserved ? "已被提升机交接预约为接货端" :
                device.InTransfer ? $"正在和提升机交接（指令 {device.Instruction}）" :
                device.WaitingDownstream ? $"指令 {device.Instruction} 停着等待中（{device.Note}）" :
                null;
            if (reason != null)
            {
                return reason;
            }
            device.Instruction = (ushort)Math.Clamp(instruction, 0, 65535);
            device.Destination = destination;
            device.Action = (byte)(type == 2 ? 2 : 1);   // 2 = 反转，其余按正转走
            device.State = 1;
            device.BusyTicks = RunTicks;
            device.WaitingDownstream = false;
            device.Note = null;
            device.CargoDirty = true;
            return null;
        }

        /// <summary>
        /// 本节货到端头：到终点就报完成；经提升机的路线交给交接逻辑；否则交给「下一节」。
        /// 交不出去时停在端头等（日志只记一次）。返回 true 表示状态有变化。
        /// </summary>
        private bool TryHandOver(ConveyorDevice device)
        {
            // 正在和提升机交接（等提升机、向提升机送货），由交接逻辑推进
            if (Hoists != null && Hoists.Owns(device))
            {
                return false;
            }
            if (!device.HasCargo)
            {
                // 收到指令但本节没货：不空跑、不报完成，等货送来（货送来时会带上它自己的指令号和目标）
                return Wait(device, "无货，等来货", $"输送线{device.DeviceId} 收到指令 {device.Instruction} 但本节没货，不运行，等货送来");
            }
            if (device.Destination == device.DeviceId)
            {
                Finish(device, "到达目标，货停在本节");
                return true;
            }
            // 路线经过提升机（层间线 ↔ 一楼输送线），由交接逻辑接管：发请求、等提升机、同时输送
            if (Hoists != null && Hoists.TryRoute(device))
            {
                return true;
            }

            var next = device.Next > 0 ? _find(device.Next) : null;
            if (next == null)
            {
                if (device.Destination > 0 && _find(device.Destination) != null)
                {
                    // 目标就在本机设备表里，但顺着「下一节」到不了：配置问题，停住，不报假完成
                    return Wait(device, "下一节配置到不了目标",
                        $"输送线{device.DeviceId} 没有配下一节（或下一节 {device.Next} 不在表里），到不了目标 {device.Destination}，请改「下一节」");
                }
                // 目标不在本机模拟范围（真实站台等）：货离开模拟范围，按到达处理
                device.HasCargo = false;
                Finish(device, device.Destination > 0 ? $"目标 {device.Destination} 不在本机设备表里，货离开模拟范围" : "没有目标、没有下一节，货离开模拟范围");
                return true;
            }
            if (next.HasCargo || next.State != 0 || next.Reserved)
            {
                string why = next.HasCargo ? "有货" : next.State == 1 ? "在运行" : next.State == 2 ? "完成未清" :
                    next.State >= 30 ? $"故障 {next.State}" : "被提升机预约";
                return Wait(device, $"等 {next.DeviceId}", $"输送线{device.DeviceId} 货到端头，等下一节 {next.DeviceId}（{why}）空出来");
            }

            Deliver(device, next);
            _log($"输送线{device.DeviceId} → {next.DeviceId} 货已交接（指令号 {next.Instruction}，最终目标 {next.Destination}，" +
                $"{(next.Destination == next.DeviceId ? "到达" : "路过")}）");
            Release(device, $"货已交给 {next.DeviceId}");
            return true;
        }

        /// <summary>停着等：上报空闲（0）、方向 0，有货位和指令号保留；状态说明 + 日志只在原因变化时记一次</summary>
        private bool Wait(ConveyorDevice device, string note, string message)
        {
            device.BusyTicks = 0;
            device.WaitingDownstream = true;
            if (device.State == 1)
            {
                device.State = 0;
                device.CargoDirty = true;
            }
            if (device.Note == note)
            {
                return false;
            }
            device.Note = note;
            device.Action = 0;
            device.CargoDirty = true;
            _log(message);
            return true;
        }

        /// <summary>交货给相邻的下一节：指令号、最终目标和方向跟着货走，下一节开始运行</summary>
        public void Deliver(ConveyorDevice from, ConveyorDevice to)
        {
            byte action = from.Action == 0 ? (byte)1 : from.Action;
            Receive(to, from.Instruction, from.Destination, action, continueRun: true);
            from.HasCargo = false;
        }

        /// <summary>
        /// 一节收到货（从上一节或从提升机）：带上指令号与目标。是终点就报完成；不是就开始运行往下走。
        /// continueRun = true 表示货刚进本节、还要走完本节长度；从提升机接货时接货过程已经把货送到位，传 false。
        /// </summary>
        public void Receive(ConveyorDevice to, ushort instruction, int destination, byte action, bool continueRun)
        {
            to.InTransfer = false;
            to.HasCargo = true;
            to.Instruction = instruction;
            to.Destination = destination;
            to.Action = action;
            to.WaitingDownstream = false;
            to.Note = null;
            to.CargoDirty = true;
            if (!continueRun && destination == to.DeviceId)
            {
                Finish(to, "从提升机接货到位，到达目标");
                return;
            }
            to.State = 1;
            to.BusyTicks = RunTicks;
        }

        /// <summary>
        /// 货已交出去、本节不是终点：不报完成，回空闲并清掉指令号（指令只由终点那一节报完成）。
        /// </summary>
        public void Release(ConveyorDevice device, string why)
        {
            var instruction = device.Instruction;
            device.HasCargo = false;
            device.State = 0;
            device.Instruction = 0;
            device.Destination = 0;
            device.Action = 0;
            device.BusyTicks = 0;
            device.WaitingDownstream = false;
            device.Note = null;
            device.InTransfer = false;
            device.CargoDirty = true;
            _log($"输送线{device.DeviceId} 回空闲（{why}；不是终点不报完成，指令号 {instruction} 由终点报完成）");
        }

        /// <summary>报完成：货停在本节，保持完成状态，等 WCS 写应答后由 PLC 清除 DB 区回空闲</summary>
        public void Finish(ConveyorDevice device, string why)
        {
            device.State = 2;
            device.Action = 0;
            device.BusyTicks = 0;
            device.WaitingDownstream = false;
            device.Note = null;
            device.CargoDirty = true;
            _log($"输送线{device.DeviceId} 报完成：指令号 {device.Instruction}（{why}），等 WCS 应答后清除");
        }
    }
}
