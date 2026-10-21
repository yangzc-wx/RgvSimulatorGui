using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Kean.Tools.RgvSimulatorGui
{
    /// <summary>
    /// Modbus TCP 从站模拟器（模拟智世 RGV 的 PLC）
    /// </summary>
    /// <remarks>
    /// 支持功能码 03、16。开启 RgvBehavior 后按《RGV 通讯协议 V1.1》模拟车辆：
    /// 写入 update(Wm016)=1 后清零 update，随后 work(Wm200) 0→1→0，并按任务类型更新反馈寄存器。
    /// </remarks>
    public sealed class ModbusTcpSlave : IDisposable
    {
        // 寄存器数：四向车只用 0–212，提升机反馈区在 46138–46159，故整体留到 46200（每台约 92 KB），超出地址返回异常码 2
        public const int REGISTER_COUNT = 46200;

        /// <summary>写入记录队列上限：只用于观察最近报文，超过就丢最旧的，避免长期运行内存只涨不降</summary>
        private const int MAX_QUEUED_WRITES = 256;

        // 下发地址（表 1-1）
        public const int TASK_TYPE = 0, START_QR = 1, INNER_QR = 2, END_QR = 3, DIRECTION = 8, PALLET_PLACE = 9, CHARGE_ON = 12, UPDATE = 16;
        // 反馈地址（表 1-2）
        public const int WORK = 200, LOCATION = 201, BATTERY = 202, WARN_TYPE = 204, STATUS = 205, WARN_CODE = 206, CARGO_STATUS = 207;

        private static readonly RgvTrack Track = new(); // 二维码轨道布局（逐格推进用）
        private readonly ushort[] _registers = new ushort[REGISTER_COUNT];
        private readonly List<Socket> _clients = new();
        private TcpListener _listener;
        private int _accepted;
        private int _resets;
        private int _dropResponses;
        private int _delayNextResponse;
        private int _taskGeneration; // 急停（taskType=7）时 +1，让正在执行的任务就地停下

        private readonly IPAddress _address; // 监听地址

        /// <param name="port">监听端口，0 表示由系统分配</param>
        /// <param name="unitId">站号</param>
        /// <param name="address">监听地址，缺省为全部地址；本机自检传 127.0.0.1，不对外暴露端口</param>
        public ModbusTcpSlave(int port = 0, byte unitId = 1, IPAddress address = null)
        {
            Port = port;
            UnitId = unitId;
            _address = address ?? IPAddress.Any;
        }

        /// <summary>监听端口（传 0 时启动后为实际端口）</summary>
        public int Port { get; private set; }

        /// <summary>站号，不匹配的请求不应答</summary>
        public byte UnitId { get; }

        /// <summary>是否模拟 RGV 任务执行</summary>
        public bool RgvBehavior { get; set; } = true;

        /// <summary>work=1 持续时间（毫秒）</summary>
        public int WorkDuration { get; set; } = 300;

        /// <summary>taskType=1 行驶时逐格推进，每格停留毫秒数</summary>
        public int StepDuration { get; set; } = 200;

        /// <summary>空顶：顶升时不置顶部托盘检测位（cargoStatus bit6），模拟顶起但没托到货</summary>
        public bool EmptyLift { get; set; }

        /// <summary>充电未打通：打开充电开关时只置 status bit4，不置 bit5</summary>
        public bool ChargeNotConnected { get; set; }

        /// <summary>收到请求不应答（模拟超时）</summary>
        public bool Mute { get; set; }

        /// <summary>响应逐字节发送（模拟 TCP 分包）</summary>
        public bool SplitResponse { get; set; }

        /// <summary>接下来 N 个请求不应答（模拟偶发丢包）</summary>
        public int DropResponses
        {
            get => _dropResponses;
            set => _dropResponses = value;
        }

        /// <summary>下一个响应延迟发送的毫秒数（模拟迟到响应），发送后自动归零</summary>
        public int DelayNextResponse
        {
            get => _delayNextResponse;
            set => _delayNextResponse = value;
        }

        /// <summary>累计接受的连接数</summary>
        public int Accepted => _accepted;

        /// <summary>当前连接数</summary>
        public int ClientCount
        {
            get { lock (_clients) return _clients.Count; }
        }

        /// <summary>被客户端以 RST 断开的连接数</summary>
        public int Resets => _resets;

        /// <summary>收到的写请求（起始地址、值）</summary>
        public ConcurrentQueue<(ushort Address, ushort[] Values)> Writes { get; } = new();

        /// <summary>最后一个请求的原始报文</summary>
        public byte[] LastRequestFrame { get; private set; }

        /// <summary>收到写请求时发生</summary>
        public event Action<ushort, ushort[]> Written;

        public ushort this[int address]
        {
            get { lock (_registers) return _registers[address]; }
            set { lock (_registers) _registers[address] = value; }
        }

        public void Start()
        {
            // 独占绑定：Windows 上 ReuseAddress 会让两个监听静默共用同一端口（车/提升机端口撞了也不报错，
            // 连接落到谁身上全凭运气）；独占后端口被占用会直接抛错。断线后同端口重启不受 TIME_WAIT 影响（已实测）
            _listener = new TcpListener(_address, Port) { ExclusiveAddressUse = true };
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            var listener = _listener;
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    Socket client;
                    try
                    {
                        client = await listener.AcceptSocketAsync();
                    }
                    catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
                    {
                        // 排队中的连接在接受前被客户端重置，跳过继续接受
                        continue;
                    }
                    catch
                    {
                        return; // 监听已停止
                    }
                    Interlocked.Increment(ref _accepted);
                    lock (_clients) _clients.Add(client);
                    _ = Task.Run(() => Serve(client));
                }
            });
        }

        /// <summary>停止监听并断开全部连接（模拟 PLC 断电）</summary>
        public void Stop()
        {
            _listener?.Stop();
            _listener = null;
            DropClients();
        }

        /// <summary>断开全部连接，保持监听（模拟 PLC 主动断开）</summary>
        public void DropClients()
        {
            lock (_clients)
            {
                foreach (var client in _clients)
                {
                    try { client.Shutdown(SocketShutdown.Both); } catch { }
                    client.Dispose();
                }
                _clients.Clear();
            }
        }

        public void Dispose() => Stop();

        private async Task Serve(Socket client)
        {
            try
            {
                var header = new byte[7];
                while (true)
                {
                    await ReceiveExactly(client, header);
                    int length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4));
                    if (length < 2 || length > 254)
                    {
                        break;
                    }
                    var pdu = new byte[length - 1];
                    await ReceiveExactly(client, pdu);
                    var frame = new byte[7 + pdu.Length];
                    header.CopyTo(frame, 0);
                    pdu.CopyTo(frame, 7);
                    LastRequestFrame = frame;
                    if (header[6] != UnitId || Mute)
                    {
                        continue;
                    }
                    var reply = Handle(pdu);
                    if (Volatile.Read(ref _dropResponses) > 0 && Interlocked.Decrement(ref _dropResponses) >= 0)
                    {
                        continue;
                    }
                    var delay = Interlocked.Exchange(ref _delayNextResponse, 0);
                    if (delay > 0)
                    {
                        await Task.Delay(delay);
                    }
                    var response = new byte[7 + reply.Length];
                    Array.Copy(header, response, 4);
                    BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(4), (ushort)(reply.Length + 1));
                    response[6] = header[6];
                    reply.CopyTo(response, 7);
                    if (SplitResponse)
                    {
                        for (int i = 0; i < response.Length; i++)
                        {
                            await client.SendAsync(response.AsMemory(i, 1), SocketFlags.None);
                            await Task.Delay(1);
                        }
                    }
                    else
                    {
                        await client.SendAsync(response, SocketFlags.None);
                    }
                }
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
            {
                Interlocked.Increment(ref _resets);
            }
            catch
            {
            }
            finally
            {
                lock (_clients) _clients.Remove(client);
                client.Dispose();
            }
        }

        private static async Task ReceiveExactly(Socket socket, byte[] buffer)
        {
            int offset = 0;
            while (offset < buffer.Length)
            {
                int length = await socket.ReceiveAsync(buffer.AsMemory(offset), SocketFlags.None);
                if (length == 0)
                {
                    throw new IOException("连接已被对端正常关闭。");
                }
                offset += length;
            }
        }

        private byte[] Handle(byte[] pdu)
        {
            byte function = pdu[0];
            switch (function)
            {
                case 0x03:
                    {
                        if (pdu.Length != 5) return Error(function, 3);
                        int address = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(1));
                        int count = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(3));
                        if (count < 1 || count > 125) return Error(function, 3);
                        if (address + count > REGISTER_COUNT) return Error(function, 2);
                        var reply = new byte[2 + count * 2];
                        reply[0] = function;
                        reply[1] = (byte)(count * 2);
                        lock (_registers)
                        {
                            for (int i = 0; i < count; i++)
                            {
                                BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(2 + i * 2), _registers[address + i]);
                            }
                        }
                        return reply;
                    }
                case 0x10:
                    {
                        if (pdu.Length < 6) return Error(function, 3);
                        int address = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(1));
                        int count = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(3));
                        if (count < 1 || count > 123 || pdu[5] != count * 2 || pdu.Length != 6 + count * 2) return Error(function, 3);
                        if (address + count > REGISTER_COUNT) return Error(function, 2);
                        var values = new ushort[count];
                        for (int i = 0; i < count; i++)
                        {
                            values[i] = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(6 + i * 2));
                        }
                        lock (_registers)
                        {
                            values.CopyTo(_registers, address);
                        }
                        Writes.Enqueue(((ushort)address, values));
                        while (Writes.Count > MAX_QUEUED_WRITES)
                        {
                            Writes.TryDequeue(out _);
                        }
                        Written?.Invoke((ushort)address, values);
                        if (RgvBehavior && address <= UPDATE && address + count > UPDATE && values[UPDATE - address] == 1)
                        {
                            Execute();
                        }
                        var reply = new byte[5];
                        Array.Copy(pdu, reply, 5);
                        return reply;
                    }
                default:
                    return Error(function, 1);
            }
        }

        private static byte[] Error(byte function, byte code) => new[] { (byte)(function | 0x80), code };

        /*
         * 模拟 RGV 执行任务：清零 update，work 0→1→0，按任务类型更新反馈
         */
        private void Execute()
        {
            ushort taskType, startQr, endQr, palletPlace, chargeOn, direction;
            lock (_registers)
            {
                _registers[UPDATE] = 0;
                taskType = _registers[TASK_TYPE];
                startQr = _registers[START_QR];
                endQr = _registers[END_QR];
                palletPlace = _registers[PALLET_PLACE];
                chargeOn = _registers[CHARGE_ON];
                direction = _registers[DIRECTION];
                if (taskType == 6)
                {
                    _registers[WARN_TYPE] = 0;
                    _registers[WARN_CODE] = 0;
                    return;
                }
                if (taskType == 7)
                {
                    // 急停：正在执行的任务就地停下（停在当前码值，不再推进、不再改反馈）
                    _taskGeneration++;
                    _registers[WORK] = 0;
                    return;
                }
                if (_registers[WORK] != 0 || _registers[WARN_TYPE] != 0)
                {
                    return;
                }
            }
            int generation = Volatile.Read(ref _taskGeneration);
            _ = Task.Run(async () =>
            {
                await Task.Delay(100);
                lock (_registers)
                {
                    if (_taskGeneration != generation)
                    {
                        return;
                    }
                    _registers[WORK] = 1;
                }
                if (taskType == 1)
                {
                    await MoveStepByStep(startQr, endQr, direction, generation);
                }
                else
                {
                    await Task.Delay(WorkDuration);
                }
                lock (_registers)
                {
                    if (_taskGeneration != generation)
                    {
                        return; // 执行期间被急停
                    }
                    switch (taskType)
                    {
                        case 1:
                            _registers[LOCATION] = endQr;
                            // 轮系：方向 1/2（码 X 向）为主通道模式，车自行换到换向位；方向 3/4（码 Y 向）从换向位出发时车自行换到中间位。
                            // 顶升位不会自己降（载货沿 Y 向行驶）；托盘检测位不变（载货在主通道上托盘仍在车上）
                            if (direction is 1 or 2)
                            {
                                _registers[STATUS] = (ushort)(_registers[STATUS] & ~0b111 | 0b100);
                            }
                            else if (direction is 3 or 4 && (_registers[STATUS] & 0b100) != 0)
                            {
                                _registers[STATUS] = (ushort)(_registers[STATUS] & ~0b111 | 0b001);
                            }
                            break;
                        case 2:
                            // Bit-0 中间位、Bit-1 顶升位、Bit-2 换向位
                            int bit = palletPlace switch { 1 => 1, 2 => 0, 3 => 2, _ => -1 };
                            if (bit >= 0)
                            {
                                _registers[STATUS] = (ushort)(_registers[STATUS] & ~0b111 | 1 << bit);
                                // cargoStatus Bit-6 顶部托盘检测位：顶升时置 1（空顶时不置），回中位/换向位时清 0（简化，不模拟车停在托盘下方）
                                _registers[CARGO_STATUS] = (ushort)(bit == 1 && !EmptyLift ? _registers[CARGO_STATUS] | 0b100_0000 : _registers[CARGO_STATUS] & ~0b100_0000);
                            }
                            break;
                        case 5:
                            // Bit-4 充电打开
                            //_registers[STATUS] = (ushort)(chargeOn == 1 ? _registers[STATUS] | 0b1_0000 : _registers[STATUS] & ~0b1_0000);
                            // Bit-4 充电开关、Bit-5 电池反馈充电正常；充电未打通时只置 Bit-4
                            int chargeBits = ChargeNotConnected ? 0b01_0000 : 0b11_0000;
                            _registers[STATUS] = (ushort)(chargeOn == 1 ? _registers[STATUS] & ~0b11_0000 | chargeBits : _registers[STATUS] & ~0b11_0000);
                            break;
                    }
                    _registers[WORK] = 0;
                }
            });
        }

        private async Task MoveStepByStep(ushort startQr, ushort endQr, ushort direction, int generation)
        {
            int current = startQr;
            int target = endQr;
            int guard = 0;
            while (current != target && guard++ < 10000)
            {
                if (!Track.TryStep(current, direction, out int next))
                {
                    next = target > current ? current + 1 : current - 1;
                }
                await Task.Delay(StepDuration);
                lock (_registers)
                {
                    if (_taskGeneration != generation)
                    {
                        return; // 急停：停在当前格
                    }
                    current = next;
                    _registers[LOCATION] = (ushort)current;
                }
            }
        }
    }
}
