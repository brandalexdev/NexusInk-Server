using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MatePadServer
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }

    public enum AppLanguage { English, Spanish }

    public class MainForm : Form
    {
        private Label lblStatus, lblUsbBadge, lblIps, lblMonTitle, lblQualTitle, lblTelemetry, lblUsbTip;
        private ComboBox cmbMonitors, cmbQuality, cmbLanguage;
        private IntPtr penDevice;
        private bool wasTouching = false;
        private Rectangle activeBounds;

        // Current Language State (Default: English for global comprehension)
        private AppLanguage currentLang = AppLanguage.English;

        // Streaming & Performance settings
        private long jpegQuality = 85L;
        private int currentTargetWidth = 1920;
        private int currentTargetHeight = 1080;
        private int targetFrameIntervalMs = 16; // 60 FPS for USB
        private long lastTelemetryUpdateTick = 0;
        private int lastPressurePct = 0, lastPx = 0, lastPy = 0, lastTiltX = 0, lastTiltY = 0;

        // Dynamic Network & USB state
        private string currentUsbIp = null;
        private string currentUsbAdapterName = null;
        private IPAddress usbGatewayIp = null;
        private bool isAdbConnected = false;
        private string currentAdbDeviceName = null;
        private List<string> cachedIps = new List<string>();
        private bool isClientConnected = false;
        private string connectedClientInfo = null;

        private const int PT_PEN = 3;
        [Flags] private enum POINTER_FLAGS : uint { NONE = 0, INRANGE = 2, INCONTACT = 4, FIRSTBUTTON = 16, PRIMARY = 8192, DOWN = 65536, UPDATE = 131072, UP = 262144 }
        [Flags] private enum PEN_FLAGS : uint { NONE = 0, BARREL = 1, ERASER = 4 }
        [Flags] private enum PEN_MASK : uint { NONE = 0, PRESSURE = 1, TILT_X = 4, TILT_Y = 8 }

        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)]
        private struct POINTER_INFO
        {
            public uint pointerType, pointerId, frameId;
            public POINTER_FLAGS pointerFlags;
            public IntPtr sourceDevice, hwndTarget;
            public POINT ptPixelLocation, ptHimetricLocation, ptPixelLocationRaw, ptHimetricLocationRaw;
            public uint dwTime, historyCount;
            public int InputData;
            public uint dwKeyStates;
            public ulong PerformanceCount;
            public int ButtonChangeType;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINTER_PEN_INFO
        {
            public POINTER_INFO pointerInfo;
            public PEN_FLAGS penFlags;
            public PEN_MASK penMask;
            public uint pressure, rotation;
            public int tiltX, tiltY;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINTER_TYPE_INFO { public uint type; public POINTER_PEN_INFO penInfo; }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr CreateSyntheticPointerDevice(uint pointerType, ulong maxCount, uint mode);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool InjectSyntheticPointerInput(IntPtr device, [In] POINTER_TYPE_INFO[] pointerInfo, uint count);

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        public MainForm()
        {
            Text = "NexusInk-server [Direct USB & Wi-Fi]";
            Size = new Size(490, 395);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            BackColor = Color.FromArgb(18, 18, 24);
            ForeColor = Color.White;

            lblStatus = new Label
            {
                Text = "Waiting for MatePad / Tablet connection...",
                Location = new Point(20, 14),
                Size = new Size(340, 24),
                Font = new Font("Segoe UI", 10, FontStyle.Bold),
                ForeColor = Color.Gold
            };

            cmbLanguage = new ComboBox
            {
                Location = new Point(370, 12),
                Size = new Size(95, 26),
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(32, 32, 42),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9)
            };
            cmbLanguage.Items.AddRange(new object[] { "🌐 English", "🌐 Español" });
            cmbLanguage.SelectedIndex = 0; // Default English for global comprehension
            cmbLanguage.SelectedIndexChanged += (s, e) =>
            {
                currentLang = (AppLanguage)cmbLanguage.SelectedIndex;
                ApplyLanguage();
            };

            lblUsbBadge = new Label
            {
                Text = "⚡ Searching for direct USB cable connection...",
                Location = new Point(20, 44),
                Size = new Size(445, 28),
                Font = new Font("Segoe UI", 9, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(8, 0, 0, 0),
                BackColor = Color.FromArgb(28, 28, 36),
                ForeColor = Color.Silver
            };

            lblIps = new Label
            {
                Text = "Local IPs: Detecting...",
                Location = new Point(20, 78),
                Size = new Size(445, 22),
                Font = new Font("Segoe UI", 9),
                ForeColor = Color.LightSkyBlue
            };

            lblMonTitle = new Label
            {
                Text = "Active Monitor (Drawing & Screen Stream):",
                Location = new Point(20, 106),
                Size = new Size(445, 18),
                Font = new Font("Segoe UI", 9)
            };

            cmbMonitors = new ComboBox
            {
                Location = new Point(20, 126),
                Size = new Size(445, 28),
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(32, 32, 42),
                ForeColor = Color.White
            };
            activeBounds = Screen.AllScreens[0].Bounds;
            cmbMonitors.SelectedIndexChanged += (s, e) =>
            {
                if (cmbMonitors.SelectedIndex >= 0 && cmbMonitors.SelectedIndex < Screen.AllScreens.Length)
                {
                    activeBounds = Screen.AllScreens[cmbMonitors.SelectedIndex].Bounds;
                }
            };

            lblQualTitle = new Label
            {
                Text = "Stream Quality & Performance Profile:",
                Location = new Point(20, 164),
                Size = new Size(445, 18),
                Font = new Font("Segoe UI", 9)
            };

            cmbQuality = new ComboBox
            {
                Location = new Point(20, 184),
                Size = new Size(445, 28),
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(32, 32, 42),
                ForeColor = Color.White
            };
            cmbQuality.SelectedIndexChanged += (s, e) => UpdateQualitySettings();

            lblTelemetry = new Label
            {
                Text = "Pressure: 0% | Pos: (0, 0) | Tilt: (0°, 0°)",
                Location = new Point(20, 224),
                Size = new Size(445, 22),
                Font = new Font("Consolas", 9),
                ForeColor = Color.LightGray
            };

            lblUsbTip = new Label
            {
                Text = "💡 Direct USB Connection (No private network needed): Enable 'USB Debugging' on your MatePad and connect the cable. In the tablet app, connect to 127.0.0.1.",
                Location = new Point(20, 252),
                Size = new Size(445, 46),
                Font = new Font("Segoe UI", 8),
                ForeColor = Color.FromArgb(170, 175, 195)
            };

            Controls.AddRange(new Control[] {
                lblStatus, cmbLanguage, lblUsbBadge, lblIps,
                lblMonTitle, cmbMonitors, lblQualTitle, cmbQuality,
                lblTelemetry, lblUsbTip
            });

            // Initialize monitors and quality list for current language
            PopulateMonitorList();
            PopulateQualityList();
            UpdateQualitySettings();

            penDevice = CreateSyntheticPointerDevice(PT_PEN, 1, 1);
            if (penDevice == IntPtr.Zero)
            {
                lblStatus.Text = currentLang == AppLanguage.English
                    ? "Windows Ink Error (Run as Administrator)"
                    : "Error de Windows Ink (Ejecutar como Administrador)";
                lblStatus.ForeColor = Color.Red;
                return;
            }

            // Automatic firewall rule self-configuration (runs silently with Administrator privileges)
            EnsureFirewallRules();

            // Initial network adapter check
            CheckNetworkInterfaces();

            // React dynamically to network plug / unplug events
            NetworkChange.NetworkAddressChanged += (s, e) => CheckNetworkInterfaces();
            NetworkChange.NetworkAvailabilityChanged += (s, e) => CheckNetworkInterfaces();

            // Regular polling timer to detect network changes
            System.Windows.Forms.Timer netTimer = new System.Windows.Forms.Timer();
            netTimer.Interval = 2500;
            netTimer.Tick += (s, e) => CheckNetworkInterfaces();
            netTimer.Start();

            // Background Workers
            _ = Task.Run(RunDiscoveryResponder);
            _ = Task.Run(RunDiscoveryTcpResponder);
            _ = Task.Run(RunPenUdpListener);
            _ = Task.Run(RunPenTcpListener);
            _ = Task.Run(RunScreenStreamer);
            _ = Task.Run(RunAdbAutoConfigAsync);
        }

        private void PopulateMonitorList()
        {
            int prevIdx = cmbMonitors.SelectedIndex;
            cmbMonitors.Items.Clear();
            for (int i = 0; i < Screen.AllScreens.Length; i++)
            {
                var s = Screen.AllScreens[i];
                string primaryTag = s.Primary
                    ? (currentLang == AppLanguage.English ? "(Primary)" : "(Principal)")
                    : "";
                string monWord = currentLang == AppLanguage.English ? "Monitor" : "Monitor";
                cmbMonitors.Items.Add($"{monWord} {i + 1}: {s.Bounds.Width}x{s.Bounds.Height} {primaryTag}".Trim());
            }
            cmbMonitors.SelectedIndex = (prevIdx >= 0 && prevIdx < cmbMonitors.Items.Count) ? prevIdx : 0;
        }

        private void PopulateQualityList()
        {
            int prevIdx = cmbQuality.SelectedIndex;
            cmbQuality.Items.Clear();
            if (currentLang == AppLanguage.English)
            {
                cmbQuality.Items.AddRange(new object[] {
                    "⚡ USB Turbo Mode (60 FPS - JPEG 85% - Latency <1ms)",
                    "Balanced HD (60 FPS - JPEG 75% - USB / 5GHz Wi-Fi)",
                    "Normal HD (35 FPS - JPEG 70% - Standard Wi-Fi)",
                    "Fast Performance (30 FPS - 1440x810 - Weak Wi-Fi)"
                });
            }
            else
            {
                cmbQuality.Items.AddRange(new object[] {
                    "⚡ Modo USB Turbo (60 FPS - JPEG 85% - Latencia <1ms)",
                    "HD Equilibrado (60 FPS - JPEG 75% - USB / Wi-Fi 5GHz)",
                    "HD Normal (35 FPS - JPEG 70% - Wi-Fi Estándar)",
                    "Rendimiento Rápido (30 FPS - 1440x810 - Wi-Fi Débil)"
                });
            }
            cmbQuality.SelectedIndex = (prevIdx >= 0 && prevIdx < cmbQuality.Items.Count) ? prevIdx : 0;
        }

        private void ApplyLanguage()
        {
            Text = currentLang == AppLanguage.English
                ? "NexusInk-server [Direct USB & Wi-Fi]"
                : "NexusInk-server [USB Directo & Wi-Fi]";

            lblMonTitle.Text = currentLang == AppLanguage.English
                ? "Active Monitor (Drawing & Screen Stream):"
                : "Monitor Activo (Dibujo y Transmisión de Pantalla):";

            lblQualTitle.Text = currentLang == AppLanguage.English
                ? "Stream Quality & Performance Profile:"
                : "Perfil de Rendimiento y Calidad de Streaming:";

            lblUsbTip.Text = currentLang == AppLanguage.English
                ? "💡 Direct USB Connection (No private network needed): Enable 'USB Debugging' on your MatePad and connect the cable. In the tablet app, connect to 127.0.0.1."
                : "💡 Conexión por cable USB directa (sin red privada ni anclaje): Activa 'Depuración USB' en tu MatePad y conecta el cable. En la app de la tablet conéctate a 127.0.0.1.";

            PopulateMonitorList();
            PopulateQualityList();
            UpdateUsbStatusUi();
            UpdateIpDisplay();
            UpdateStatusDisplay();
            UpdateTelemetryDisplay();
        }

        private void UpdateStatusDisplay()
        {
            if (isClientConnected && !string.IsNullOrEmpty(connectedClientInfo))
            {
                string connType = isAdbConnected ? (currentLang == AppLanguage.English ? "⚡ Direct USB Cable" : "⚡ Cable USB Directo")
                                                 : (currentLang == AppLanguage.English ? "📶 Wi-Fi" : "📶 Wi-Fi");
                lblStatus.Text = currentLang == AppLanguage.English
                    ? $"Streaming screen ({connectedClientInfo}) [{connType}]"
                    : $"Transmitiendo pantalla ({connectedClientInfo}) [{connType}]";
                lblStatus.ForeColor = Color.SpringGreen;
            }
            else
            {
                lblStatus.Text = currentLang == AppLanguage.English
                    ? "Waiting for MatePad / Tablet connection..."
                    : "Esperando conexión de MatePad / Tablet...";
                lblStatus.ForeColor = Color.Gold;
            }
        }

        private void UpdateIpDisplay()
        {
            string prefix = currentLang == AppLanguage.English ? "Local IPs: " : "IPs Locales: ";
            string noConn = currentLang == AppLanguage.English ? "No connection" : "Sin conexión";
            lblIps.Text = prefix + (cachedIps.Count > 0 ? string.Join(" | ", cachedIps) : noConn);
        }

        private void UpdateTelemetryDisplay()
        {
            lblTelemetry.Text = currentLang == AppLanguage.English
                ? $"Pressure: {lastPressurePct}% | Pos: ({lastPx}, {lastPy}) | Tilt: ({lastTiltX}°, {lastTiltY}°)"
                : $"Presión: {lastPressurePct}% | Pos: ({lastPx}, {lastPy}) | Inclinación: ({lastTiltX}°, {lastTiltY}°)";
        }

        private void UpdateQualitySettings()
        {
            switch (cmbQuality.SelectedIndex)
            {
                case 0: // USB Turbo
                    jpegQuality = 85L;
                    currentTargetWidth = 1920;
                    currentTargetHeight = 1080;
                    targetFrameIntervalMs = 16; // 60 FPS
                    break;
                case 1: // Balanced HD
                    jpegQuality = 75L;
                    currentTargetWidth = 1920;
                    currentTargetHeight = 1080;
                    targetFrameIntervalMs = 16; // 60 FPS
                    break;
                case 2: // Normal HD
                    jpegQuality = 70L;
                    currentTargetWidth = 1920;
                    currentTargetHeight = 1080;
                    targetFrameIntervalMs = 28; // ~35 FPS
                    break;
                case 3: // Fast
                    jpegQuality = 60L;
                    currentTargetWidth = 1440;
                    currentTargetHeight = 810;
                    targetFrameIntervalMs = 33; // ~30 FPS
                    break;
            }
        }

        private void CheckNetworkInterfaces()
        {
            try
            {
                string usbIpFound = null;
                string usbName = null;
                IPAddress gatewayFound = null;
                List<string> allIps = new List<string>();

                var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(ni => ni.OperationalStatus == OperationalStatus.Up && ni.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                    .ToList();

                foreach (var ni in interfaces)
                {
                    bool isUsb = IsUsbTetheringInterface(ni);
                    var ipProps = ni.GetIPProperties();
                    foreach (var unicast in ipProps.UnicastAddresses)
                    {
                        if (unicast.Address.AddressFamily == AddressFamily.InterNetwork)
                        {
                            string ipStr = unicast.Address.ToString();
                            allIps.Add(ipStr);
                            if (isUsb && usbIpFound == null)
                            {
                                usbIpFound = ipStr;
                                usbName = ni.Name;

                                var gw = ipProps.GatewayAddresses
                                    .FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork);
                                if (gw != null)
                                {
                                    gatewayFound = gw.Address;
                                }
                            }
                        }
                    }
                }

                currentUsbIp = usbIpFound;
                currentUsbAdapterName = usbName;
                if (gatewayFound != null)
                {
                    usbGatewayIp = gatewayFound;
                }
                cachedIps = allIps;

                if (IsHandleCreated)
                {
                    BeginInvoke(new Action(() =>
                    {
                        UpdateIpDisplay();
                        UpdateUsbStatusUi();
                    }));
                }
            }
            catch { }
        }

        private bool IsUsbTetheringInterface(NetworkInterface ni)
        {
            string desc = (ni.Description ?? "").ToLowerInvariant();
            string name = (ni.Name ?? "").ToLowerInvariant();

            if (desc.Contains("remote ndis") || desc.Contains("rndis") || desc.Contains("ndis compatible") ||
                desc.Contains("usb") || desc.Contains("cdc") || desc.Contains("android") ||
                desc.Contains("huawei") || name.Contains("rndis") || name.Contains("usb"))
            {
                return true;
            }

            foreach (var addr in ni.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                {
                    string ip = addr.Address.ToString();
                    if (ip.StartsWith("192.168.42.") || ip.StartsWith("192.168.43.") || ip.StartsWith("192.168.8."))
                        return true;
                }
            }

            return false;
        }

        private void UpdateUsbStatusUi()
        {
            if (isAdbConnected)
            {
                string dev = currentAdbDeviceName ?? "Huawei MatePad";
                lblUsbBadge.Text = currentLang == AppLanguage.English
                    ? $"⚡ USB CABLE ACTIVE ({dev}) • 60 FPS (<1ms)"
                    : $"⚡ CABLE USB ACTIVO ({dev}) • 60 FPS (<1ms)";
                lblUsbBadge.ForeColor = Color.SpringGreen;
                lblUsbBadge.BackColor = Color.FromArgb(20, 48, 28);

                if (cmbQuality.SelectedIndex > 1)
                {
                    cmbQuality.SelectedIndex = 0;
                }
            }
            else if (!string.IsNullOrEmpty(currentUsbIp))
            {
                lblUsbBadge.Text = currentLang == AppLanguage.English
                    ? $"⚡ USB CABLE ACTIVE ({currentUsbIp}) • Turbo Mode <1ms"
                    : $"⚡ CABLE USB ACTIVO ({currentUsbIp}) • Modo Turbo <1ms";
                lblUsbBadge.ForeColor = Color.SpringGreen;
                lblUsbBadge.BackColor = Color.FromArgb(20, 48, 28);

                if (cmbQuality.SelectedIndex > 1)
                {
                    cmbQuality.SelectedIndex = 0;
                }
            }
            else
            {
                lblUsbBadge.Text = currentLang == AppLanguage.English
                    ? "📶 Wi-Fi Mode (Plug in USB cable with Debugging enabled for Turbo Mode)"
                    : "📶 Modo Wi-Fi (Conecta el cable USB con Depuración activa para Modo Turbo)";
                lblUsbBadge.ForeColor = Color.Goldenrod;
                lblUsbBadge.BackColor = Color.FromArgb(36, 32, 18);
            }
        }

        private bool IsUsbOrLocal(IPAddress addr)
        {
            if (IPAddress.IsLoopback(addr)) return true;
            string s = addr.ToString();
            if (!string.IsNullOrEmpty(currentUsbIp))
            {
                string[] parts = currentUsbIp.Split('.');
                if (parts.Length == 4 && s.StartsWith($"{parts[0]}.{parts[1]}.{parts[2]}."))
                    return true;
            }
            return s.StartsWith("192.168.42.") || s.StartsWith("192.168.43.") || s.StartsWith("192.168.8.");
        }

        private async Task RunScreenStreamer()
        {
            TcpListener listener = new TcpListener(IPAddress.Any, 5002);
            listener.Start();

            ImageCodecInfo jpegCodec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);

            while (true)
            {
                try
                {
                    TcpClient client = await listener.AcceptTcpClientAsync();
                    client.NoDelay = true;
                    client.SendBufferSize = 131072; // 128 KB for smooth USB throughput

                    IPEndPoint remoteEp = (IPEndPoint)client.Client.RemoteEndPoint;
                    bool isUsbConnection = IsUsbOrLocal(remoteEp.Address);

                    isClientConnected = true;
                    connectedClientInfo = remoteEp.Address.ToString();

                    BeginInvoke(new Action(UpdateStatusDisplay));

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            using NetworkStream stream = client.GetStream();
                            byte[] lenBuf = new byte[4];
                            Stopwatch frameTimer = new Stopwatch();

                            while (client.Connected)
                            {
                                frameTimer.Restart();
                                Rectangle bounds = activeBounds;
                                int targetW = currentTargetWidth;
                                int targetH = currentTargetHeight;

                                byte[] jpgBytes;
                                using (EncoderParameters encParams = new EncoderParameters(1))
                                {
                                    encParams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, jpegQuality);

                                    // Direct fast path: If bounds match target exactly, capture without resize
                                    if (bounds.Width == targetW && bounds.Height == targetH)
                                    {
                                        using Bitmap rawBmp = new Bitmap(targetW, targetH, PixelFormat.Format32bppRgb);
                                        using (Graphics gRaw = Graphics.FromImage(rawBmp))
                                        {
                                            gRaw.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
                                        }

                                        using MemoryStream ms = new MemoryStream();
                                        rawBmp.Save(ms, jpegCodec, encParams);
                                        jpgBytes = ms.ToArray();
                                    }
                                    else
                                    {
                                        using Bitmap rawBmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppRgb);
                                        using (Graphics gRaw = Graphics.FromImage(rawBmp))
                                        {
                                            gRaw.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
                                        }

                                        using Bitmap scaledBmp = new Bitmap(targetW, targetH, PixelFormat.Format24bppRgb);
                                        using (Graphics gScaled = Graphics.FromImage(scaledBmp))
                                        {
                                            gScaled.InterpolationMode = InterpolationMode.Bilinear;
                                            gScaled.CompositingQuality = CompositingQuality.HighSpeed;
                                            gScaled.SmoothingMode = SmoothingMode.None;
                                            gScaled.DrawImage(rawBmp, 0, 0, targetW, targetH);
                                        }

                                        using MemoryStream ms = new MemoryStream();
                                        scaledBmp.Save(ms, jpegCodec, encParams);
                                        jpgBytes = ms.ToArray();
                                    }
                                }

                                BinaryPrimitives.WriteInt32BigEndian(lenBuf, jpgBytes.Length);
                                await stream.WriteAsync(lenBuf, 0, 4);
                                await stream.WriteAsync(jpgBytes, 0, jpgBytes.Length);

                                int frameTarget = (isUsbConnection || cmbQuality.SelectedIndex <= 1) ? targetFrameIntervalMs : 28;
                                int elapsed = (int)frameTimer.ElapsedMilliseconds;
                                int delay = Math.Max(1, frameTarget - elapsed);
                                await Task.Delay(delay);
                            }
                        }
                        catch { client.Close(); }
                        finally
                        {
                            isClientConnected = false;
                            connectedClientInfo = null;
                            if (IsHandleCreated) BeginInvoke(new Action(UpdateStatusDisplay));
                        }
                    });
                }
                catch { }
            }
        }

        private async Task RunPenUdpListener()
        {
            using UdpClient udp = new UdpClient();
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.ReceiveBufferSize = 65536;
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, 5000));

            while (true)
            {
                try
                {
                    var res = await udp.ReceiveAsync();
                    ProcessPenData(res.Buffer);
                }
                catch { }
            }
        }

        private async Task RunPenTcpListener()
        {
            TcpListener tcp = new TcpListener(IPAddress.Any, 5000);
            tcp.Start();

            while (true)
            {
                try
                {
                    TcpClient client = await tcp.AcceptTcpClientAsync();
                    client.NoDelay = true;
                    client.ReceiveBufferSize = 65536;

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            using NetworkStream ns = client.GetStream();
                            byte[] buf = new byte[28];
                            while (client.Connected)
                            {
                                int read = 0;
                                while (read < 4)
                                {
                                    int r = await ns.ReadAsync(buf, read, 4 - read);
                                    if (r <= 0) return;
                                    read += r;
                                }

                                int firstVal = BinaryPrimitives.ReadInt32BigEndian(buf.AsSpan(0, 4));
                                if (firstVal >= 1 && firstVal <= 6)
                                {
                                    TriggerShortcut(firstVal);
                                    continue;
                                }

                                while (read < 28)
                                {
                                    int r = await ns.ReadAsync(buf, read, 28 - read);
                                    if (r <= 0) return;
                                    read += r;
                                }

                                ProcessPenData(buf);
                            }
                        }
                        catch { client.Close(); }
                    });
                }
                catch { }
            }
        }

        private void ProcessPenData(ReadOnlySpan<byte> d)
        {
            if (d.Length == 4)
            {
                TriggerShortcut(BinaryPrimitives.ReadInt32BigEndian(d));
                return;
            }

            if (d.Length < 28) return;

            int action = BinaryPrimitives.ReadInt32BigEndian(d.Slice(0, 4));
            float normX = BinaryPrimitives.ReadSingleBigEndian(d.Slice(4, 4));
            float normY = BinaryPrimitives.ReadSingleBigEndian(d.Slice(8, 4));
            float pressure = BinaryPrimitives.ReadSingleBigEndian(d.Slice(12, 4));
            float tilt = BinaryPrimitives.ReadSingleBigEndian(d.Slice(16, 4));
            float orientation = BinaryPrimitives.ReadSingleBigEndian(d.Slice(20, 4));
            int isEraser = BinaryPrimitives.ReadInt32BigEndian(d.Slice(24, 4));

            InjectPen(action, normX, normY, pressure, tilt, orientation, isEraser);
        }

        private void InjectPen(int action, float normX, float normY, float pressure, float tilt, float orientation, int isEraser)
        {
            int px = activeBounds.Left + (int)(normX * activeBounds.Width);
            int py = activeBounds.Top + (int)(normY * activeBounds.Height);

            POINTER_FLAGS pFlags = POINTER_FLAGS.INRANGE | POINTER_FLAGS.PRIMARY;
            if (action == 1 || (action == 2 && !wasTouching))
            {
                pFlags |= POINTER_FLAGS.INCONTACT | POINTER_FLAGS.DOWN | POINTER_FLAGS.FIRSTBUTTON;
                wasTouching = true;
            }
            else if (action == 2 && wasTouching)
            {
                pFlags |= POINTER_FLAGS.INCONTACT | POINTER_FLAGS.UPDATE | POINTER_FLAGS.FIRSTBUTTON;
            }
            else if (action == 0 && wasTouching)
            {
                pFlags |= POINTER_FLAGS.UP;
                wasTouching = false;
            }
            else
            {
                pFlags |= POINTER_FLAGS.UPDATE;
                wasTouching = false;
            }

            double tiltDeg = tilt * (180.0 / Math.PI);
            int tiltX = (int)Math.Clamp(tiltDeg * Math.Sin(orientation), -90, 90);
            int tiltY = (int)Math.Clamp(tiltDeg * Math.Cos(orientation), -90, 90);
            uint winPressure = (action == 1 || action == 2) ? (uint)Math.Clamp(pressure * 1024f, 1, 1024) : 0;

            POINTER_TYPE_INFO[] input = new POINTER_TYPE_INFO[1];
            input[0].type = PT_PEN;
            input[0].penInfo.pointerInfo.pointerType = PT_PEN;
            input[0].penInfo.pointerInfo.pointerId = 1;
            input[0].penInfo.pointerInfo.ptPixelLocation = new POINT { X = px, Y = py };
            input[0].penInfo.pointerInfo.pointerFlags = pFlags;
            input[0].penInfo.penFlags = (isEraser == 1) ? PEN_FLAGS.ERASER : PEN_FLAGS.NONE;
            input[0].penInfo.penMask = PEN_MASK.PRESSURE | PEN_MASK.TILT_X | PEN_MASK.TILT_Y;
            input[0].penInfo.pressure = winPressure;
            input[0].penInfo.tiltX = tiltX;
            input[0].penInfo.tiltY = tiltY;

            InjectSyntheticPointerInput(penDevice, input, 1);

            lastPressurePct = (int)(pressure * 100);
            lastPx = px;
            lastPy = py;
            lastTiltX = tiltX;
            lastTiltY = tiltY;

            // Throttle UI text updates to avoid message loop congestion at 120Hz/240Hz pen frequencies
            long now = Environment.TickCount64;
            if (now - lastTelemetryUpdateTick > 60)
            {
                lastTelemetryUpdateTick = now;
                if (IsHandleCreated)
                {
                    BeginInvoke(new Action(UpdateTelemetryDisplay));
                }
            }
        }

        private void TriggerShortcut(int cmd)
        {
            const byte VK_CONTROL = 0x11, VK_SHIFT = 0x10, VK_Z = 0x5A, VK_SPACE = 0x20, VK_MENU = 0x12, VK_B = 0x42, VK_E = 0x45;
            const uint KEYUP = 0x0002;

            switch (cmd)
            {
                case 1:
                    keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
                    keybd_event(VK_Z, 0, 0, UIntPtr.Zero);
                    keybd_event(VK_Z, 0, KEYUP, UIntPtr.Zero);
                    keybd_event(VK_CONTROL, 0, KEYUP, UIntPtr.Zero);
                    break;
                case 2:
                    keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
                    keybd_event(VK_SHIFT, 0, 0, UIntPtr.Zero);
                    keybd_event(VK_Z, 0, 0, UIntPtr.Zero);
                    keybd_event(VK_Z, 0, KEYUP, UIntPtr.Zero);
                    keybd_event(VK_SHIFT, 0, KEYUP, UIntPtr.Zero);
                    keybd_event(VK_CONTROL, 0, KEYUP, UIntPtr.Zero);
                    break;
                case 3:
                    keybd_event(VK_SPACE, 0, 0, UIntPtr.Zero);
                    keybd_event(VK_SPACE, 0, KEYUP, UIntPtr.Zero);
                    break;
                case 4:
                    keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
                    keybd_event(VK_MENU, 0, KEYUP, UIntPtr.Zero);
                    break;
                case 5:
                    keybd_event(VK_B, 0, 0, UIntPtr.Zero);
                    keybd_event(VK_B, 0, KEYUP, UIntPtr.Zero);
                    break;
                case 6:
                    keybd_event(VK_E, 0, 0, UIntPtr.Zero);
                    keybd_event(VK_E, 0, KEYUP, UIntPtr.Zero);
                    break;
            }
        }

        private async Task RunDiscoveryResponder()
        {
            using UdpClient disc = new UdpClient();
            disc.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            disc.Client.Bind(new IPEndPoint(IPAddress.Any, 5001));

            byte[] ok = Encoding.UTF8.GetBytes("MATEPAD_INK_SERVER_OK");

            // Direct USB beacon sender: periodically announces server presence to USB gateway / subnet
            _ = Task.Run(async () =>
            {
                using UdpClient beaconClient = new UdpClient();
                beaconClient.EnableBroadcast = true;
                byte[] beaconMsg = Encoding.UTF8.GetBytes("MATEPAD_INK_SERVER_OK");

                while (true)
                {
                    try
                    {
                        if (usbGatewayIp != null)
                        {
                            await beaconClient.SendAsync(beaconMsg, beaconMsg.Length, new IPEndPoint(usbGatewayIp, 5001));
                        }

                        if (!string.IsNullOrEmpty(currentUsbIp))
                        {
                            string[] parts = currentUsbIp.Split('.');
                            if (parts.Length == 4)
                            {
                                IPAddress subnetBcast = IPAddress.Parse($"{parts[0]}.{parts[1]}.{parts[2]}.255");
                                await beaconClient.SendAsync(beaconMsg, beaconMsg.Length, new IPEndPoint(subnetBcast, 5001));
                            }
                        }
                    }
                    catch { }

                    await Task.Delay(2000);
                }
            });

            while (true)
            {
                try
                {
                    var req = await disc.ReceiveAsync();
                    string msg = Encoding.UTF8.GetString(req.Buffer);
                    if (msg == "DISCOVER_MATEPAD_SERVER" || msg == "MATEPAD_INK_DISCOVER" || msg == "DISCOVER_NEXUS_SERVER")
                    {
                        await disc.SendAsync(ok, ok.Length, req.RemoteEndPoint);
                        if (IsHandleCreated)
                        {
                            BeginInvoke(new Action(() =>
                            {
                                string connType = IsUsbOrLocal(req.RemoteEndPoint.Address)
                                    ? (currentLang == AppLanguage.English ? "⚡ Direct USB Cable" : "⚡ Cable USB Directo")
                                    : (currentLang == AppLanguage.English ? "📶 Wi-Fi" : "📶 Wi-Fi");

                                lblStatus.Text = currentLang == AppLanguage.English
                                    ? $"Connected to MatePad via {connType} ({req.RemoteEndPoint.Address})"
                                    : $"Conectado a MatePad vía {connType} ({req.RemoteEndPoint.Address})";
                                lblStatus.ForeColor = Color.SpringGreen;
                            }));
                        }
                    }
                }
                catch { }
            }
        }

        private async Task RunDiscoveryTcpResponder()
        {
            TcpListener tcp = new TcpListener(IPAddress.Any, 5001);
            tcp.Start();
            byte[] ok = Encoding.UTF8.GetBytes("MATEPAD_INK_SERVER_OK\n");

            while (true)
            {
                try
                {
                    TcpClient client = await tcp.AcceptTcpClientAsync();
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            using NetworkStream ns = client.GetStream();
                            await ns.WriteAsync(ok, 0, ok.Length);
                        }
                        catch { }
                        finally { client.Close(); }
                    });
                }
                catch { }
            }
        }

        private async Task RunAdbAutoConfigAsync()
        {
            string adbPath = FindAdbPath();
            if (string.IsNullOrEmpty(adbPath)) return;

            bool lastAttached = false;
            while (true)
            {
                try
                {
                    string deviceModel = await CheckAdbDeviceAttachedAsync(adbPath);
                    bool attached = !string.IsNullOrEmpty(deviceModel);
                    if (attached && !lastAttached)
                    {
                        currentAdbDeviceName = deviceModel;

                        // Automatically forward ports over the physical USB cable
                        await ExecuteAdbCommandAsync(adbPath, "reverse tcp:5002 tcp:5002");
                        await ExecuteAdbCommandAsync(adbPath, "reverse tcp:5000 tcp:5000");
                        await ExecuteAdbCommandAsync(adbPath, "reverse tcp:5001 tcp:5001");

                        isAdbConnected = true;
                        if (IsHandleCreated)
                        {
                            BeginInvoke(new Action(UpdateUsbStatusUi));
                        }
                    }
                    else if (!attached && lastAttached)
                    {
                        isAdbConnected = false;
                        currentAdbDeviceName = null;
                        if (IsHandleCreated)
                        {
                            BeginInvoke(new Action(UpdateUsbStatusUi));
                        }
                    }
                    lastAttached = attached;
                }
                catch { }

                await Task.Delay(2000);
            }
        }

        private string FindAdbPath()
        {
            try
            {
                string localAdb = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "adb.exe");
                if (File.Exists(localAdb)) return localAdb;

                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string sdkAdb = Path.Combine(localAppData, "Android", "Sdk", "platform-tools", "adb.exe");
                if (File.Exists(sdkAdb)) return sdkAdb;

                using Process p = new Process();
                p.StartInfo = new ProcessStartInfo("where.exe", "adb")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                p.Start();
                string line = p.StandardOutput.ReadLine();
                p.WaitForExit();
                if (!string.IsNullOrWhiteSpace(line) && File.Exists(line.Trim()))
                    return line.Trim();
            }
            catch { }

            return null;
        }

        private async Task<string> CheckAdbDeviceAttachedAsync(string adbPath)
        {
            try
            {
                using Process p = new Process();
                p.StartInfo = new ProcessStartInfo(adbPath, "devices -l")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                p.Start();
                string output = await p.StandardOutput.ReadToEndAsync();
                await p.WaitForExitAsync();

                var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    string trimmed = line.Trim();
                    if (trimmed.StartsWith("*") || trimmed.StartsWith("List of devices")) continue;

                    if (Regex.IsMatch(trimmed, @"\s+device(\s+|$)"))
                    {
                        var match = Regex.Match(trimmed, @"model:(\S+)");
                        if (match.Success)
                        {
                            return match.Groups[1].Value.Replace("_", " ");
                        }
                        return "MatePad USB";
                    }
                }
            }
            catch { }
            return null;
        }

        private async Task ExecuteAdbCommandAsync(string adbPath, string arguments)
        {
            try
            {
                using Process p = new Process();
                p.StartInfo = new ProcessStartInfo(adbPath, arguments)
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                p.Start();
                await p.WaitForExitAsync();
            }
            catch { }
        }

        private static void EnsureFirewallRules()
        {
            Task.Run(() =>
            {
                try
                {
                    RunNetsh("advfirewall firewall add rule name=\"NexusInk UDP (Stylus & AutoConnect)\" dir=in action=allow protocol=UDP localport=5000,5001 profile=any");
                    RunNetsh("advfirewall firewall add rule name=\"NexusInk TCP (Screen & ADB)\" dir=in action=allow protocol=TCP localport=5000,5001,5002 profile=any");
                }
                catch { }
            });
        }

        private static void RunNetsh(string args)
        {
            try
            {
                using Process p = new Process();
                p.StartInfo = new ProcessStartInfo("netsh", args)
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                p.Start();
                p.WaitForExit(3000);
            }
            catch { }
        }
    }
}