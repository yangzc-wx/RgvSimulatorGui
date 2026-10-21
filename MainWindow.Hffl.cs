using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Opc.Ua.Configuration;
using Button = System.Windows.Controls.Button;

namespace Kean.Tools.RgvSimulatorGui
{
    /// <summary>
    /// OPC 四向车页（OPC UA 订阅协议，世仓 DB552/DB553 报文）。
    /// 和 Modbus 四向车页互不相干：各自的车、各自的服务器；只共用界面定时器和日志。
    /// </summary>
    public partial class MainWindow
    {
        private readonly ObservableCollection<HfflCarVm> _hfflCars = new();
        private readonly List<HfflRgv> _hfflDevices = new();   // 当前 OPC 服务器上的车（运行态）
        private readonly Stopwatch _hfflClock = new();          // 两次推进之间的真实间隔（定时器会有抖动）
        private HfflRgvOpcServer _hfflServer;
        private string _hfflEndpoint = "";
        private int _hfflSessions;
        private int _hfflStepMs = 1000, _hfflWorkMs = 1500;
        private int _hfflManualId = 60000;                      // 手工命令的序列号（避开 RCS 常用的小号段）

        private HfflCarVm SelectedHfflCar => HfflGrid.SelectedItem as HfflCarVm;

        private void InitHffl()
        {
            HfflGrid.ItemsSource = _hfflCars;
            HfflGrid.SelectionChanged += (_, __) =>
            {
                if (SelectedHfflCar is HfflCarVm car)
                {
                    HfflPositionBox.Text = car.Position;
                }
                RefreshStatus();
            };
        }

        /// <summary>一个界面周期：每台车推进一步，反馈帧变了就推给订阅方</summary>
        private void TickHffl()
        {
            if (_hfflServer == null)
            {
                return;
            }
            int elapsed = (int)Math.Clamp(_hfflClock.ElapsedMilliseconds, 0, 2000);
            _hfflClock.Restart();
            bool heartbeat = HfflHeartToggle.IsChecked == true;
            foreach (var vm in _hfflCars)
            {
                vm.Car.Tick(elapsed, _hfflStepMs, _hfflWorkMs, heartbeat);
                _hfflServer.NodeManager?.PushReadValue(vm.Car);
                vm.Refresh();
            }
        }

        private void RefreshHfflStatus()
        {
            HfflStartBtn.IsEnabled = true;
            HfflStopBtn.IsEnabled = _hfflServer != null;
            HfflOpsPanel.IsEnabled = SelectedHfflCar != null;
            HfflEmptyHint.Visibility = _hfflCars.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void RefreshHfflNavStatus()
        {
            if (_hfflServer == null)
            {
                HfflNavStatus.Text = "未启动";
                return;
            }
            int sessions = _hfflServer.SessionCount;
            HfflNavStatus.Text = $"{_hfflCars.Count} 台 · {_hfflCars.Count(c => c.IsConnected)} 已订阅 · 会话 {sessions}";
            HfflEndpointText.Text = $"运行中 · {_hfflEndpoint} · ns={_hfflServer.NodeManager?.RgvNamespaceIndex} · 会话 {sessions}";
            if (sessions != _hfflSessions)
            {
                int before = _hfflSessions;
                _hfflSessions = sessions;
                Log(sessions switch
                {
                    0 => $"OPC 四向车：会话已全部断开（原有 {before} 个）",
                    var s when s > before => $"OPC 四向车：有客户端连上（会话 {s}；RCS 每台车一个会话）",
                    _ => $"OPC 四向车：有客户端断开，还剩 {sessions} 个会话"
                });
            }
        }

        private void RefreshHfflOpsTitle()
        {
            HfflOpsTitle.Text = SelectedHfflCar is HfflCarVm car
                ? $"车 {car.Device} · {car.Position} · {car.Server} · {car.StateText}"
                : "选中车操作（先在上表选中一行）";
        }

        // ===== 启停 =====

        private async void OnHfflStartClick(object sender, RoutedEventArgs e)
        {
            if (_hfflServer != null &&
                !await ConfirmAsync("按参数重建", $"当前有 {_hfflCars.Count} 台 OPC 四向车在跑。启动会先全部停掉，再按参数重建，继续？", "重建"))
            {
                return;
            }
            await StartHfflAsync();
        }

        private void OnHfflStopClick(object sender, RoutedEventArgs e) => StopHffl();

        private async Task StartHfflAsync()
        {
            StopHffl();
            if (!TryBuildHfflCars(out var cars))
            {
                return;
            }
            _hfflDevices.AddRange(cars);

            int port = Math.Clamp(ParseInt(HfflPortBox.Text, 49321), 1, 65535);
            string pkiRoot = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RgvSimulatorGui", "pki");
            var config = HfflRgvOpcServer.BuildConfiguration(port, pkiRoot);
            HfflStartBtn.IsEnabled = false;
            if (!await TryStartHfflServerAsync(config, pkiRoot, port))
            {
                return;
            }
            // RCS 把命名空间索引硬编码成 3，没落到 3 就重启一次再试（同输送线）
            if (_hfflServer.NodeManager.RgvNamespaceIndex != 3)
            {
                Log($"OPC 四向车：命名空间索引落在 ns={_hfflServer.NodeManager.RgvNamespaceIndex}（RCS 要 3），重启一次 OPC 服务器再试……");
                try { _hfflServer.Stop(); } catch { }
                _hfflServer = null;
                if (!await TryStartHfflServerAsync(config, pkiRoot, port))
                {
                    return;
                }
            }

            foreach (var car in _hfflDevices)
            {
                _hfflCars.Add(new HfflCarVm(car, Log, c => _hfflServer?.NodeManager?.PushReadValue(c),
                    c => _hfflServer?.NodeManager?.SubscriberCount(c) ?? 0));
                _hfflServer.NodeManager.PushReadValue(car);
            }
            ApplyHfflDurations();
            _hfflClock.Restart();
            int ns = _hfflServer.NodeManager.RgvNamespaceIndex;
            _hfflEndpoint = $"opc.tcp://127.0.0.1:{port}";
            _hfflSessions = 0;
            var first = _hfflDevices[0];
            var last = _hfflDevices[^1];
            Log($"OPC 四向车已启动：{_hfflEndpoint}，{_hfflDevices.Count} 台（{first.DeviceId}-{last.DeviceId}），命名空间索引 ns={ns}");
            Log($"RCS 的 appsettings.json 要把 Connections:OpcUa 里 {first.Server} … {last.Server} 都指到 {_hfflEndpoint}（右上角「复制 appsettings」可直接拷）");
            Log($"RCS 订阅：ns={ns};s={first.ReadNodeId}（{HfflRgv.FEEDBACK_LENGTH} 字节）；下发写：ns={ns};s={first.WriteNodeId}（{HfflRgv.COMMAND_LENGTH} 字节）");
            Log($"命名空间表：{_hfflServer.NodeManager.DescribeNamespaces()}");
            if (ns != 3)
            {
                Log("注意：命名空间索引不是 3，RCS 取节点会报 BadNodeIdUnknown。点「停止」再「启动」一次通常能落到 3");
            }
            if (_hfflCars.Count > 0)
            {
                HfflGrid.SelectedIndex = 0;
            }
            RefreshStatus();
        }

        /// <summary>按界面参数建车；参数不对记日志返回 false</summary>
        private bool TryBuildHfflCars(out List<HfflRgv> cars)
        {
            cars = new List<HfflRgv>();
            int startId = Math.Clamp(ParseInt(HfflStartIdBox.Text, 1001), 1, 65535);
            int count = Math.Clamp(ParseInt(HfflCountBox.Text, 13), 1, 50);
            int firstIndex = Math.Clamp(ParseInt(HfflServerIndexBox.Text, 1), 0, 99999);
            string prefix = HfflServerPrefixBox.Text ?? "";
            string readTag = (HfflReadTagBox.Text ?? "").Trim();
            string writeTag = (HfflWriteTagBox.Text ?? "").Trim();
            if (prefix.Trim().Length == 0 || readTag.Length == 0 || writeTag.Length == 0)
            {
                Log("OPC 四向车：连接名前缀、读标签、写标签都不能为空");
                return false;
            }
            if (readTag == writeTag)
            {
                Log("OPC 四向车：读标签和写标签不能相同（DB553 是反馈、DB552 是命令）");
                return false;
            }

            var positions = new List<(int Tier, int Bay, int Row)>();
            foreach (var part in (HfflPositionsBox.Text ?? "").Split(new[] { ',', '，', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (HfflRgv.TryParseCell(part, out int tier, out int bay, out int row))
                {
                    positions.Add((tier, bay, row));
                }
                else
                {
                    Log($"OPC 四向车：初始位置「{part}」不是「层-列-排」格式，跳过");
                }
            }
            if (positions.Count == 0)
            {
                positions.Add((1, 1, 1));
            }
            var added = new List<string>();
            while (positions.Count < count)
            {
                // 不够就沿最后一个位置往后顺延一列（真实货位不一定存在，RCS 会在日志里提示"当前车位置不存在"）
                var (tier, bay, row) = positions[^1];
                positions.Add((tier, bay + 1, row));
                added.Add(HfflRgv.CellText(tier, bay + 1, row));
            }
            if (added.Count > 0)
            {
                Log($"OPC 四向车：初始位置不够 {count} 个，按最后一个往后顺延补了：{string.Join("、", added)}（最好填 RCS 车表里的真实位置）");
            }

            var nodeIds = new HashSet<string>();
            for (int i = 0; i < count; i++)
            {
                var car = new HfflRgv(startId + i, $"{prefix}{firstIndex + i}", readTag, writeTag, Log)
                {
                    Tier = positions[i].Tier,
                    Bay = positions[i].Bay,
                    Row = positions[i].Row
                };
                if (!nodeIds.Add(car.ReadNodeId) || !nodeIds.Add(car.WriteNodeId))
                {
                    Log($"OPC 四向车：车{car.DeviceId} 的节点和前面的车重复（{car.Server}），检查连接名前缀/起始序号");
                    return false;
                }
                cars.Add(car);
            }
            return true;
        }

        /// <summary>起一次 OPC 服务器；失败时把日志和运行态收拾干净，返回 false（证书处理同输送线）</summary>
        private async Task<bool> TryStartHfflServerAsync(Opc.Ua.ApplicationConfiguration config, string pkiRoot, int port)
        {
            try
            {
                var app = new ApplicationInstance
                {
                    ApplicationName = config.ApplicationName,
                    ApplicationType = Opc.Ua.ApplicationType.Server,
                    ApplicationConfiguration = config
                };
                if (!await app.CheckApplicationInstanceCertificate(false, 2048))
                {
                    Log("OPC 四向车：OPC 应用证书创建失败，未启动");
                    StopHffl();
                    return false;
                }
                if (config.ApplicationUri != HfflRgvOpcServer.ApplicationUriValue)
                {
                    Log($"OPC 四向车：证书里的应用 URI 是 {config.ApplicationUri}，与配置的 {HfflRgvOpcServer.ApplicationUriValue} 不一致，删掉旧证书重新生成");
                    config.ApplicationUri = HfflRgvOpcServer.ApplicationUriValue;
                    DeleteHfflCertificateFiles(pkiRoot);
                    await app.CheckApplicationInstanceCertificate(false, 2048);
                    config.ApplicationUri = HfflRgvOpcServer.ApplicationUriValue;
                }
                config.CertificateValidator.CertificateValidation += (_, args) => args.Accept = true;
                _hfflServer = new HfflRgvOpcServer(_hfflDevices);
                _hfflServer.Start(config);
                _hfflServer.NodeManager.CommandReceived += OnHfflCommand;
                return true;
            }
            catch (Exception ex)
            {
                string hint = ex.InnerException?.Message ?? "";
                if (ex.InnerException?.InnerException != null)
                {
                    hint += " → " + ex.InnerException.InnerException.Message;
                }
                Log($"OPC 四向车服务器启动失败：{ex.Message}（端口 {port} 被占用？和输送线的 OPC 端口不能相同）" + (hint.Length > 0 ? $"，原因：{hint}" : ""));
                StopHffl();
                return false;
            }
        }

        /// <summary>只删本页自己那套自签名证书（pki\own 下 RgvSimulatorGuiHfflRgv* 的文件），输送线的证书不动</summary>
        private static void DeleteHfflCertificateFiles(string pkiRoot)
        {
            foreach (var dir in new[] { System.IO.Path.Combine(pkiRoot, "own", "certs"), System.IO.Path.Combine(pkiRoot, "own", "private") })
            {
                if (!System.IO.Directory.Exists(dir))
                {
                    continue;
                }
                foreach (var file in System.IO.Directory.GetFiles(dir, HfflRgvOpcServer.CertificateName + "*"))
                {
                    try { System.IO.File.Delete(file); } catch { }
                }
            }
        }

        private void StopHffl()
        {
            bool wasRunning = _hfflServer != null;
            if (_hfflServer != null)
            {
                try { _hfflServer.NodeManager.CommandReceived -= OnHfflCommand; } catch { }
                try { _hfflServer.Stop(); } catch { }
                _hfflServer = null;
            }
            _hfflCars.Clear();
            _hfflDevices.Clear();
            _hfflSessions = 0;
            HfflEndpointText.Text = "车辆状态";
            if (wasRunning)
            {
                Log("OPC 四向车服务器已停止");
            }
            RefreshStatus();
        }

        // ===== 命令 =====

        /// <summary>RCS 往写节点写命令（OPC 服务器线程）：转回界面线程再处理，避免和定时器同时改车的状态</summary>
        private void OnHfflCommand(HfflRgv car, byte[] frame)
        {
            var copy = (byte[])frame.Clone();
            Dispatcher.BeginInvoke(new Action(() => HandleHfflCommand(car, copy)));
        }

        private void HandleHfflCommand(HfflRgv car, byte[] frame)
        {
            if (!_hfflDevices.Contains(car))
            {
                return;   // 服务器已停 / 已重启，旧车的命令丢掉
            }
            car.HandleCommand(frame);
            _hfflServer?.NodeManager?.PushReadValue(car);   // 状态 1（接收）立即推，不等下一个周期
            _hfflCars.FirstOrDefault(vm => vm.Car == car)?.Refresh();
            RefreshHfflOpsTitle();
        }

        /// <summary>选中车面板「手工下命令」：拼一帧和 RCS 一样的 110 字节，写进写区节点并按收到命令处理</summary>
        private void OnHfflSendClick(object sender, RoutedEventArgs e)
        {
            var vm = SelectedHfflCar;
            if (vm == null || _hfflServer == null)
            {
                Log("先启动 OPC 四向车，并在表里选中一行");
                return;
            }
            int type = (HfflCmdTypeBox.SelectedItem as ComboBoxItem)?.Tag is string tag && int.TryParse(tag, out var t) ? t : 1;
            var car = vm.Car;
            int tier = car.Tier, bay = car.Bay, row = car.Row;
            if (type is 1 or 2 && !HfflRgv.TryParseCell(HfflCmdTargetBox.Text, out tier, out bay, out row))
            {
                Log("手工命令：终点要填「层-列-排」，例如 01-10-13");
                return;
            }
            int id = _hfflManualId = _hfflManualId >= 65535 ? 60000 : _hfflManualId + 1;
            var frame = HfflCommand.Build(id, type, id, car.Tier, car.Bay, car.Row, tier, bay, row);
            _hfflServer.NodeManager.PushWriteValue(car, frame);
            Log($"[OPC车{car.DeviceId}] 手工下发（等同 RCS 写 DB552）：");
            HandleHfflCommand(car, frame);
        }

        private void OnHfflActionClick(object sender, RoutedEventArgs e)
        {
            var vm = SelectedHfflCar;
            if (vm == null || _hfflServer == null)
            {
                Log("先启动 OPC 四向车，并在表里选中一行");
                return;
            }
            var car = vm.Car;
            switch ((sender as Button)?.Tag as string)
            {
                case "checkfail":
                    car.FailNextCheck = true;
                    Log($"OPC车{car.DeviceId}：下一条命令将校验失败（状态 5）");
                    break;
                case "execfail":
                    car.FailNextExec = true;
                    Log($"OPC车{car.DeviceId}：下一条执行完的命令将报执行失败（状态 6）");
                    break;
                case "abort":
                    car.Abort();
                    break;
                case "fault":
                    car.Normal = false;
                    car.FaultCode1 |= 1;
                    Log($"OPC车{car.DeviceId} 置故障：工作状态位清 0（RCS 判为故障 30），故障码1 bit0 = 1（RCS 报警编号 1）");
                    break;
                case "clear":
                    car.Normal = true;
                    car.FaultCode1 = 0;
                    car.FaultCode2 = 0;
                    Log($"OPC车{car.DeviceId} 清故障：工作状态位置 1，故障码清零");
                    break;
                case "showfeedback":
                    HfflFrameBox.Text = string.Join(",", car.BuildFrame());
                    return;
                case "showcommand":
                    HfflFrameBox.Text = car.LastCommandFrame is { Length: > 0 } frame ? string.Join(",", frame) : "";
                    if (HfflFrameBox.Text.Length == 0)
                    {
                        Log($"OPC车{car.DeviceId} 还没收到过命令");
                    }
                    return;
                case "loadfeedback":
                    if (!TryParseByteList(HfflFrameBox.Text, out var bytes) || bytes.Length == 0)
                    {
                        Log("反馈帧要填逗号分隔的十进制字节（0-255），可以先点「看反馈帧」再改");
                        return;
                    }
                    car.LoadFrame(bytes);
                    Log($"OPC车{car.DeviceId} 反馈帧（DB553）手工改成 {bytes.Length} 字节：命令 #{car.CommandId} 状态 {car.CommandState}，位置 {car.Position}，托盘 {(car.Pallet ? 1 : 0)}，自动 {(car.Auto ? 1 : 0)}，正常 {(car.Normal ? 1 : 0)}");
                    break;
            }
            _hfflServer.NodeManager.PushReadValue(car);
            vm.Refresh();
            RefreshHfflOpsTitle();
        }

        private void OnHfflSetPositionClick(object sender, RoutedEventArgs e)
        {
            if (SelectedHfflCar is not HfflCarVm vm)
            {
                Log("先在 OPC 四向车表里选中一行");
                return;
            }
            vm.Position = HfflPositionBox.Text;
        }

        private void OnHfflPositionKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                OnHfflSetPositionClick(sender, e);
            }
        }

        /// <summary>按当前参数生成 appsettings 的 Connections:OpcUa 条目，复制到剪贴板</summary>
        private void OnHfflCopySettingsClick(object sender, RoutedEventArgs e)
        {
            int port = Math.Clamp(ParseInt(HfflPortBox.Text, 49321), 1, 65535);
            int count = Math.Clamp(ParseInt(HfflCountBox.Text, 13), 1, 50);
            int firstIndex = Math.Clamp(ParseInt(HfflServerIndexBox.Text, 1), 0, 99999);
            string prefix = HfflServerPrefixBox.Text ?? "";
            var text = new StringBuilder();
            for (int i = 0; i < count; i++)
            {
                text.Append($"      \"{prefix}{firstIndex + i}\": \"opc.tcp://127.0.0.1:{port}\"");
                text.AppendLine(i + 1 < count ? "," : "");
            }
            try
            {
                Clipboard.SetText(text.ToString());
                Log($"已复制 {count} 行 appsettings 配置（{prefix}{firstIndex} … {prefix}{firstIndex + count - 1} → opc.tcp://127.0.0.1:{port}），粘到 Connections:OpcUa 里替换原来的车连接");
            }
            catch (Exception ex)
            {
                Log($"复制失败：{ex.Message}");
            }
        }

        // ===== 时长 =====

        private void OnHfflDurationCommit(object sender, RoutedEventArgs e) => ApplyHfflDurations();

        private void OnHfflDurationKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                ApplyHfflDurations();
            }
        }

        private void ApplyHfflDurations()
        {
            int step = Math.Clamp(ParseInt(HfflStepBox.Text, 1000), 50, 60000);
            int work = Math.Clamp(ParseInt(HfflWorkBox.Text, 1500), 0, 60000);
            HfflStepBox.Text = step.ToString();
            HfflWorkBox.Text = work.ToString();
            if (step == _hfflStepMs && work == _hfflWorkMs)
            {
                return;
            }
            _hfflStepMs = step;
            _hfflWorkMs = work;
            if (_hfflServer != null)
            {
                Log($"OPC 四向车时长已生效：{step}ms/格，{work}ms/动作（换向、取放货、充电）");
            }
        }

        /// <summary>把 "0,1,2" 解析成字节；空串得到空数组</summary>
        private static bool TryParseByteList(string text, out byte[] bytes)
        {
            bytes = Array.Empty<byte>();
            var parts = (text ?? "").Split(new[] { ',', ' ', ';', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var list = new List<byte>(parts.Length);
            foreach (var part in parts)
            {
                if (!byte.TryParse(part.Trim(), out var b))
                {
                    return false;
                }
                list.Add(b);
            }
            bytes = list.ToArray();
            return true;
        }
    }
}
