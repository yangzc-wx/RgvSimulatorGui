using System;
using System.Collections.Generic;
using System.Linq;

namespace Kean.Tools.RgvSimulatorGui
{
    /// <summary>提升机的一个对接口：哪一层、哪一侧接着哪条输送线</summary>
    internal sealed record HoistDock(int Hoist, int Layer, string Side, int Conveyor)
    {
        public const string RackSide = "货架侧";
        public const string FloorSide = "一楼线侧";
        public bool IsFloor => Side == FloorSide;
    }

    /// <summary>
    /// 模拟“输送线 PLC ↔ 提升机 PLC”之间的交接（它们自己的通道，不经 WCS）。WCS 只给输送线下“起点 → 终点”。
    /// <para>一次交接的完整顺序（日志按这些信号名打印）：</para>
    /// <para>1. 起点输送线货到端头 → 停止输送 → C3 请求送货。停着等提升机期间上报“空闲（0）+ 有货 + 保留指令号”，不接 WCS 指令、不给允许取货。</para>
    /// <para>2. 提升机能接才接（就绪、平台无货无车、没在搬别的、接货端输送线空闲无货且未被预约），接受后**预约接货端**，
    /// 防止别的货进去；不能接则起点停在端头等，条件满足后自动接受。</para>
    /// <para>3. 提升机不在起点层就先升降过去（起点一直停着）；到层或本来就在层，经对接应答后发 L3 对接到位 + L4 允许进入。</para>
    /// <para>4. 起点收到允许才重新启动、上报运行（1），双方同时输送（L6 / C5）。货完全上平台：L7 接收到位、C7 送出完成，起点立即回空闲（可以接下一托）。</para>
    /// <para>5. 提升机载货升降到接货层（同层则原地换侧），L3 对接到位 + L5 请求送货；接货端回 C4 允许接货后启动接货（上报运行 + 指令号），双方同时输送。</para>
    /// <para>6. 货完全进入接货端：C6 接收到位、L8 送出完成，提升机释放、取消预约；接货端是终点就报完成，否则接着往下走。</para>
    /// <para>异常：提升机报警 / 暂停 / 非 WCS 模式（L9 停止请求），或正在交接的输送线故障（C8 停止请求）时，双方一起停，恢复后从断点继续。</para>
    /// <para>路线：起点和终点是同一台提升机的两个对接口时直接交接；层间线的货要去别处（出库）时，经提升机送到一楼输送线再往下走；
    /// 层间线之间、层间线和一楼输送线之间只能经提升机相通。反馈区的任务号用 100000 起的号段（输送线 PLC 的号段），不与 WCS 的 1001–30000 冲突。</para>
    /// </summary>
    internal sealed class HoistTransferLink
    {
        private enum Phase { Requesting, ToSource, Loading, ToTarget, WaitTarget, Unloading }

        private const int DockTicks = 2;   // 提升机已在本层时，从收到请求到回“对接到位 + 允许”的应答时间（约 0.5 秒）

        private sealed class Job
        {
            public int Hoist;
            public ConveyorDevice Source;
            public ConveyorDevice Target;
            public HoistDock From;
            public HoistDock To;
            public uint TaskCode;
            public ushort Instruction;   // 货的指令号、最终目标、运行方向：起点回空闲后由交接带着，送到接货端时交给它
            public int Destination;
            public byte Action;
            public Phase Phase;
            public int Ticks;
            public bool Logged;          // 当前阶段的等待原因已记过日志
            public bool Frozen;          // 因报警 / 故障停住
            public bool SourceReleased;  // 货已上平台，起点已回空闲
        }

        private readonly List<HoistDock> _docks;
        private readonly Func<int, ConveyorDevice> _findConveyor;
        private readonly Func<int, HoistBehavior> _findHoist;
        private readonly ConveyorFlow _flow;
        private readonly Action<string> _log;
        private readonly List<Job> _jobs = new();
        private readonly Dictionary<int, byte> _originalAction = new();  // 起点停下前的运行方向，提升机允许后按原方向重新启动
        private readonly Dictionary<int, string> _waitReasons = new();   // 在端头等提升机的起点 → 最近一次记过的原因
        private uint _sequence = 100000;

        public HoistTransferLink(IEnumerable<HoistDock> docks, Func<int, ConveyorDevice> findConveyor, Func<int, HoistBehavior> findHoist,
            ConveyorFlow flow, Action<string> log)
        {
            _docks = docks.ToList();
            _findConveyor = findConveyor;
            _findHoist = findHoist;
            _flow = flow;
            _log = log;
        }

        /// <summary>重庆现场的对接关系（来自前端 3D 布局 assets/settings/3d.json）：层间线在货架侧，一楼输送线在另一侧的 1 层</summary>
        public static IReadOnlyList<HoistDock> DefaultDocks { get; } = new[]
        {
            new HoistDock(13001, 1, HoistDock.RackSide, 12011), new HoistDock(13001, 2, HoistDock.RackSide, 12012), new HoistDock(13001, 3, HoistDock.RackSide, 12013),
            new HoistDock(13001, 1, HoistDock.FloorSide, 12010),
            new HoistDock(13002, 1, HoistDock.RackSide, 12021), new HoistDock(13002, 2, HoistDock.RackSide, 12022), new HoistDock(13002, 3, HoistDock.RackSide, 12023),
            new HoistDock(13002, 1, HoistDock.FloorSide, 12019)
        };

        public IReadOnlyList<HoistDock> Docks => _docks;

        /// <summary>这台输送线正由交接逻辑控制（在端头等提升机、向提升机送货、从提升机接货），普通运行逻辑和 WCS 指令都不要动它</summary>
        public bool Owns(ConveyorDevice device) =>
            _waitReasons.ContainsKey(device.DeviceId) ||
            _jobs.Any(j => (j.Source == device && !j.SourceReleased) || (j.Target == device && j.Phase == Phase.Unloading));

        /// <summary>
        /// 起点输送线货到端头、要交出去时调用：路线经过提升机就接管（返回 true），否则返回 false 走普通的「下一节」。
        /// </summary>
        public bool TryRoute(ConveyorDevice source)
        {
            var from = _docks.FirstOrDefault(d => d.Conveyor == source.DeviceId);
            if (from == null)
            {
                return false;
            }
            var to = Route(from, source);
            if (to == null)
            {
                // 一楼输送线往外走：只要「下一节」不是这台提升机的对接口，就按普通输送线走
                var next = _docks.FirstOrDefault(d => d.Conveyor == source.Next && d.Hoist == from.Hoist);
                if (next == null)
                {
                    return false;
                }
                // 「下一节」是层间线：两者只能经提升机相通，而目标又不在这台提升机上，路线配置不对
                if (source.Note != "下一节只能经提升机到达")
                {
                    source.Note = "下一节只能经提升机到达";
                    source.State = 0;                    // 停着等改配置：上报空闲，有货位和指令号保留
                    source.Action = 0;
                    source.WaitingDownstream = true;
                    source.CargoDirty = true;
                    _log($"[交接] 输送线{source.DeviceId} 的下一节 {source.Next} 只能经提升机{from.Hoist} 到达，但目标 {source.Destination} 不在这台提升机上，" +
                        "请把「下一节」改成往外走的那一节");
                }
                return true;
            }
            TryAccept(source, from, to);
            return true;
        }

        /// <summary>经提升机的接货口：目标是同一台提升机的另一个对接口就直接去；层间线的货去别处（出库）先到一楼输送线</summary>
        private HoistDock Route(HoistDock from, ConveyorDevice source)
        {
            var direct = _docks.FirstOrDefault(d => d.Hoist == from.Hoist && d.Conveyor == source.Destination && d != from);
            if (direct != null)
            {
                return direct;
            }
            return from.IsFloor ? null : _docks.FirstOrDefault(d => d.Hoist == from.Hoist && d.IsFloor);
        }

        /// <summary>请求提升机：先停在端头发 C3；能接就接受并预约接货端，不能接记原因让起点等着（每轮再试）</summary>
        private void TryAccept(ConveyorDevice source, HoistDock from, HoistDock to)
        {
            if (!_waitReasons.ContainsKey(source.DeviceId))
            {
                // 货到端头光电：先停下，再发请求；提升机允许之前不再转
                _originalAction[source.DeviceId] = source.Action == 0 ? (byte)1 : source.Action;
                source.Action = 0;
                source.State = 0;                        // 停着等：上报空闲，有货位和指令号保留
                source.InTransfer = true;
                source.BusyTicks = 0;
                source.WaitingDownstream = true;
                source.Note = "到位停止，请求提升机";
                source.CargoDirty = true;
                _waitReasons[source.DeviceId] = "";
                _log($"[交接] 输送线{source.DeviceId} 货到端头，停止输送 → C3 请求送货（提升机{from.Hoist}，送往 {to.Layer}层{to.Side} {to.Conveyor}）");
            }

            var hoist = _findHoist(from.Hoist);
            var target = _findConveyor(to.Conveyor);
            string reason =
                hoist == null ? $"提升机 {from.Hoist} 没有启动" :
                target == null ? $"接货输送线 {to.Conveyor} 不在本机设备表里" :
                _jobs.Any(j => j.Hoist == from.Hoist) ? "正在为别的输送线搬运" :
                hoist.Ready != 1 ? $"未就绪（{HoistState(hoist)}）" :
                hoist.Cargo != 1 ? "平台上有货" :
                hoist.Vehicle != 1 ? "平台上有车" :
                target.Reserved ? $"接货输送线 {to.Conveyor} 已被预约" :
                target.HasCargo ? $"接货输送线 {to.Conveyor} 有货" :
                target.State != 0 ? $"接货输送线 {to.Conveyor} {(target.State == 1 ? "在运行" : target.State == 2 ? "完成未清" : $"故障 {target.State}")}" :
                null;
            if (reason != null)
            {
                if (_waitReasons[source.DeviceId] != reason)
                {
                    _waitReasons[source.DeviceId] = reason;
                    source.Note = "到位停止，等提升机";
                    source.CargoDirty = true;
                    _log($"[交接] 提升机{from.Hoist} 暂不能接：{reason}；输送线{source.DeviceId} 停在端头等待");
                }
                return;
            }

            _waitReasons.Remove(source.DeviceId);
            var job = new Job
            {
                Hoist = from.Hoist,
                Source = source,
                Target = target,
                From = from,
                To = to,
                TaskCode = ++_sequence,
                Instruction = source.Instruction,
                Destination = source.Destination,
                Action = _originalAction.TryGetValue(source.DeviceId, out var action) ? action : (byte)1
            };
            _originalAction.Remove(source.DeviceId);
            _jobs.Add(job);
            target.Reserved = true;                      // 预约接货端：别的货不能再进去，WCS 指令也不接
            target.CargoDirty = true;
            source.Note = "到位停止，等提升机对接";
            source.CargoDirty = true;
            hoist.BeginLocal(job.TaskCode, 6);
            _log($"[交接] 提升机{from.Hoist} 接受请求（搬运号 {job.TaskCode}，指令号 {job.Instruction}），预约接货输送线 {target.DeviceId}；" +
                $"{from.Layer}层{from.Side} {source.DeviceId} → {to.Layer}层{to.Side} {target.DeviceId}");
            if (hoist.CurrentLayer != from.Layer)
            {
                _log($"[交接] 提升机{from.Hoist} 当前在 {hoist.CurrentLayer} 层，前往 {from.Layer} 层；输送线{source.DeviceId} 保持停止，提升机允许前不启动");
                hoist.MoveLocal(from.Layer);
                Enter(job, hoist, Phase.ToSource, $"前往 {from.Layer} 层接货");
            }
            else
            {
                job.Ticks = DockTicks;
                Enter(job, hoist, Phase.Requesting, $"{from.Layer} 层对接中");
            }
        }

        /// <summary>每个界面周期调用一次（在提升机 Tick 之后、输送线 Tick 之前）：重试等待中的请求，推进各台提升机的交接</summary>
        public void Tick()
        {
            foreach (var deviceId in _waitReasons.Keys.ToList())
            {
                var source = _findConveyor(deviceId);
                var from = _docks.FirstOrDefault(d => d.Conveyor == deviceId);
                var to = from == null || source == null ? null : Route(from, source);
                if (source == null || to == null || !source.HasCargo)
                {
                    // 起点被清了 / 改了目标 / 没货了：撤销请求
                    _waitReasons.Remove(deviceId);
                    _originalAction.Remove(deviceId);
                    if (source != null)
                    {
                        source.InTransfer = false;
                        source.Note = null;
                        source.CargoDirty = true;
                    }
                    _log($"[交接] 输送线{deviceId} 的请求撤销（货被清掉或目标已改）");
                    continue;
                }
                if (source.State >= 30)
                {
                    continue;                            // 起点故障：先不请求，复位后接着请求
                }
                TryAccept(source, from, to);
            }

            foreach (var job in _jobs.ToList())
            {
                var hoist = _findHoist(job.Hoist);
                if (hoist == null)
                {
                    _log($"[交接] 提升机{job.Hoist} 已停止，搬运 {job.TaskCode} 中止（输送线 {job.Source.DeviceId} → {job.Target.DeviceId}）");
                    job.Target.Reserved = false;
                    job.Target.CargoDirty = true;
                    _jobs.Remove(job);
                    continue;
                }
                string stop = StopReason(job, hoist);
                if (stop != null)
                {
                    Freeze(job, stop);
                    continue;
                }
                if (job.Frozen)
                {
                    Resume(job);
                }
                Step(job, hoist);
            }
        }

        /// <summary>需要双方停住的原因：提升机报警 / 暂停 / 非 WCS 模式（L9），或正在交接的输送线故障（C8）</summary>
        private static string StopReason(Job job, HoistBehavior hoist)
        {
            if (hoist.WarnType != 0) return $"提升机{job.Hoist} 报警 {hoist.WarnType}（L9 停止请求）";
            if (hoist.Paused == 1) return $"提升机{job.Hoist} 暂停（L9 停止请求）";
            if (hoist.Model != 1) return $"提升机{job.Hoist} 不在 WCS 模式（L9 停止请求）";
            if (job.Phase == Phase.Loading && job.Source.State >= 30) return $"输送线{job.Source.DeviceId} 故障 {job.Source.State}（C8 停止请求）";
            if (job.Phase == Phase.Unloading && job.Target.State >= 30) return $"输送线{job.Target.DeviceId} 故障 {job.Target.State}（C8 停止请求）";
            return null;
        }

        /// <summary>停住：正在输送的那条输送线也停（方向置 0），计时不走；提升机升降由它自己在报警 / 暂停时停住</summary>
        private void Freeze(Job job, string reason)
        {
            if (job.Frozen)
            {
                return;
            }
            job.Frozen = true;
            var active = job.Phase == Phase.Loading ? job.Source : job.Phase == Phase.Unloading ? job.Target : null;
            if (active != null)
            {
                active.Action = 0;
                if (active.State == 1)
                {
                    active.State = 0;                    // 停住了：上报空闲（故障的保持故障）
                }
                active.CargoDirty = true;
            }
            _log($"[交接] {reason}：搬运 {job.TaskCode} 双方停住{(active != null ? $"（输送线{active.DeviceId} 停止）" : "")}，恢复后从断点继续");
        }

        /// <summary>恢复：正在输送的那条输送线回到运行（故障复位会把它清成空闲，这里按交接状态重新置运行）</summary>
        private void Resume(Job job)
        {
            job.Frozen = false;
            var active = job.Phase == Phase.Loading ? job.Source : job.Phase == Phase.Unloading ? job.Target : null;
            if (active != null)
            {
                active.State = 1;
                active.Action = job.Action;
                active.BusyTicks = 0;
                active.CargoDirty = true;
                if (active == job.Target)
                {
                    active.Instruction = job.Instruction;
                    active.Destination = job.Destination;
                }
            }
            _log($"[交接] 搬运 {job.TaskCode} 恢复，从断点继续");
        }

        private void Step(Job job, HoistBehavior hoist)
        {
            switch (job.Phase)
            {
                case Phase.Requesting:                   // 提升机已在起点层：对接应答
                case Phase.ToSource:                     // 空平台升降到起点层，起点停着
                    if (!job.Source.HasCargo)
                    {
                        Abort(job, hoist, $"输送线{job.Source.DeviceId} 上的货被清掉");
                        return;
                    }
                    if (job.Phase == Phase.Requesting ? --job.Ticks > 0 : hoist.Moving)
                    {
                        return;
                    }
                    if (job.Source.State >= 30)
                    {
                        if (!job.Logged)
                        {
                            job.Logged = true;
                            _log($"[交接] 提升机{job.Hoist} 已到位，但输送线{job.Source.DeviceId} 故障 {job.Source.State}，不允许进入，等它复位");
                        }
                        return;
                    }
                    BeginLoading(job, hoist);
                    break;

                case Phase.Loading:                      // 起点 → 平台，双方同时输送
                    if (--job.Ticks > 0)
                    {
                        return;
                    }
                    hoist.Cargo = 2;
                    _log($"[交接] 提升机{job.Hoist} L7 接收到位；输送线{job.Source.DeviceId} C7 送出完成");
                    job.SourceReleased = true;
                    _flow.Release(job.Source, $"货已送入提升机 {job.Hoist}");
                    hoist.BeginLocal(job.TaskCode, 7);
                    if (hoist.CurrentLayer != job.To.Layer)
                    {
                        _log($"[交接] 提升机{job.Hoist} 载货从 {hoist.CurrentLayer} 层前往 {job.To.Layer} 层");
                        hoist.MoveLocal(job.To.Layer);
                        Enter(job, hoist, Phase.ToTarget, $"载货前往 {job.To.Layer} 层");
                    }
                    else
                    {
                        _log($"[交接] 提升机{job.Hoist} 接货端也在 {job.To.Layer} 层，原地换到{job.To.Side}");
                        Enter(job, hoist, Phase.WaitTarget, $"{job.To.Layer} 层换侧");
                    }
                    break;

                case Phase.ToTarget:                     // 载货升降到接货层
                    if (!hoist.Moving)
                    {
                        Enter(job, hoist, Phase.WaitTarget, $"到达 {job.To.Layer} 层");
                    }
                    break;

                case Phase.WaitTarget:                   // L3 + L5 请求送货，等接货端 C4 允许接货
                    if (job.Target.HasCargo || job.Target.State != 0)
                    {
                        // 已预约，正常不会发生；界面上手工改了有货 / 置了故障时停在这里等
                        if (!job.Logged)
                        {
                            job.Logged = true;
                            hoist.TransferText = $"等 {job.Target.DeviceId} 允许接货";
                            _log($"[交接] 提升机{job.Hoist} L3 对接到位 + L5 请求送货 → 输送线{job.Target.DeviceId} 现在不能接（{(job.Target.HasCargo ? "有货" : $"状态 {job.Target.State}")}），等待");
                        }
                        return;
                    }
                    job.Ticks = _flow.RunTicks;
                    job.Target.State = 1;                // 接货端启动接货：上报运行 + 指令号（货在往这一节进）
                    job.Target.Instruction = job.Instruction;
                    job.Target.Destination = job.Destination;
                    job.Target.Action = job.Action;
                    job.Target.BusyTicks = 0;
                    job.Target.WaitingDownstream = false;
                    job.Target.Note = "从提升机接货";
                    job.Target.CargoDirty = true;
                    Enter(job, hoist, Phase.Unloading, $"送出到 {job.Target.DeviceId}");
                    _log($"[交接] 提升机{job.Hoist} 停在 {hoist.CurrentLayer} 层：L3 对接到位（{job.To.Side}）+ L5 请求送货；" +
                        $"输送线{job.Target.DeviceId} C4 允许接货、启动接货；双方同时输送（L6 / C5）");
                    break;

                case Phase.Unloading:                    // 平台 → 接货端，双方同时输送
                    if (--job.Ticks > 0)
                    {
                        return;
                    }
                    hoist.Cargo = 1;
                    job.Target.Reserved = false;
                    _jobs.Remove(job);
                    hoist.EndLocal();
                    _log($"[交接] 输送线{job.Target.DeviceId} C6 接收到位；提升机{job.Hoist} L8 送出完成，释放（搬运号 {job.TaskCode}）");
                    _flow.Receive(job.Target, job.Instruction, job.Destination, job.Action, continueRun: false);
                    break;
            }
        }

        /// <summary>提升机到起点层：L3 对接到位 + L4 允许进入，起点收到允许后按原方向重新启动，双方同时输送</summary>
        private void BeginLoading(Job job, HoistBehavior hoist)
        {
            job.Ticks = _flow.RunTicks;
            Enter(job, hoist, Phase.Loading, $"从 {job.Source.DeviceId} 接货");
            job.Source.State = 1;                    // 收到允许、重新输送：上报运行
            job.Source.Action = job.Action;
            job.Source.WaitingDownstream = false;
            job.Source.Note = "向提升机送货";
            job.Source.CargoDirty = true;
            _log($"[交接] 提升机{job.Hoist} 停在 {hoist.CurrentLayer} 层：L3 对接到位（{job.From.Side}）+ L4 允许进入；" +
                $"输送线{job.Source.DeviceId} 收到允许后启动；双方同时输送（L6 / C5）");
        }

        /// <summary>中止还没开始送货的交接（起点的货被清掉等）：提升机释放、取消预约、起点不再交接</summary>
        private void Abort(Job job, HoistBehavior hoist, string reason)
        {
            _jobs.Remove(job);
            hoist.AbortLocal();
            job.Target.Reserved = false;
            job.Target.CargoDirty = true;
            job.Source.InTransfer = false;
            job.Source.Note = null;
            job.Source.CargoDirty = true;
            _log($"[交接] 搬运 {job.TaskCode} 中止：{reason}；提升机{job.Hoist} 释放，取消 {job.Target.DeviceId} 的预约");
        }

        private static void Enter(Job job, HoistBehavior hoist, Phase phase, string text)
        {
            job.Phase = phase;
            job.Logged = false;
            hoist.TransferText = $"{job.Source.DeviceId}→{job.Target.DeviceId}：{text}";
        }

        private static string HoistState(HoistBehavior hoist) =>
            hoist.Model != 1 ? "非 WCS 模式" :
            hoist.WarnType != 0 ? $"报警 {hoist.WarnType}" :
            hoist.Held ? "被输送线占用" :
            hoist.Busy ? "正在执行 WCS 任务" :
            "未在层位";

        /// <summary>停止提升机或输送线时清掉所有交接：释放提升机占用、取消预约</summary>
        public void Reset()
        {
            foreach (var job in _jobs)
            {
                _findHoist(job.Hoist)?.AbortLocal();
                job.Target.Reserved = false;
                job.Source.InTransfer = false;
            }
            foreach (var deviceId in _waitReasons.Keys)
            {
                var source = _findConveyor(deviceId);
                if (source != null)
                {
                    source.InTransfer = false;
                }
            }
            _jobs.Clear();
            _waitReasons.Clear();
            _originalAction.Clear();
        }
    }
}
