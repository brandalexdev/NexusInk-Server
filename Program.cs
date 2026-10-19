using System;
using System.Buffers.Binary;
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

    public class MainForm : Form
    {
        private Label lblStatus, lblIps, lblTelemetry;
        private ComboBox cmbMonitors, cmbQuality;
        private IntPtr penDevice;
        private bool wasTouching = false;
        private Rectangle activeBounds;
        private long jpegQuality = 75L;

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
            Text = "NexusInk-server";
            Size = new Size(450, 310);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            BackColor = Color.FromArgb(18, 18, 22);
            ForeColor = Color.White;

            lblStatus = new Label
            {
                Text = "Waiting for MatePad connection...",
                Location = new Point(20, 18),
                Size = new Size(400, 25),
                Font = new Font("Segoe UI", 10, FontStyle.Bold),
                ForeColor = Color.Gold
            };

            lblIps = new Label
            {
                Text = "Local IPs: " + GetLocalIps(),
                Location = new Point(20, 46),
                Size = new Size(400, 38),
                Font = new Font("Segoe UI", 9),
                ForeColor = Color.LightSkyBlue
            };

            Label lblMonTitle = new Label
            {
                Text = "Active Monitor (Drawing & Screen Stream):",
                Location = new Point(20, 90),
                Size = new Size(350, 20),
                Font = new Font("Segoe UI", 9)
            };

            cmbMonitors = new ComboBox
            {
                Location = new Point(20, 112),
                Size = new Size(390, 28),
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(32, 32, 40),
                ForeColor = Color.White
            };

            for (int i = 0; i < Screen.AllScreens.Length; i++)
            {
                var s = Screen.AllScreens[i];
                cmbMonitors.Items.Add($"Monitor {i + 1}: {s.Bounds.Width}x{s.Bounds.Height} {(s.Primary ? "(Primary)" : "")}");
            }
            cmbMonitors.SelectedIndex = 0;
            activeBounds = Screen.AllScreens[0].Bounds;
            cmbMonitors.SelectedIndexChanged += (s, e) => activeBounds = Screen.AllScreens[cmbMonitors.SelectedIndex].Bounds;

            Label lblQualTitle = new Label
            {
                Text = "Stream Quality (Resolution & Sharpness):",
                Location = new Point(20, 150),
                Size = new Size(350, 20),
                Font = new Font("Segoe UI", 9)
            };

            cmbQuality = new ComboBox
            {
                Location = new Point(20, 172),
                Size = new Size(390, 28),
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(32, 32, 40),
                ForeColor = Color.White
            };
            cmbQuality.Items.AddRange(new object[] {
                "Ultra Sharp (1920x1080 - JPEG 85% - Best for USB)",
                "Balanced HD (1920x1080 - JPEG 70% - Recommended)",
                "Fast Performance (1440x810 - JPEG 60% - Wi-Fi)"
            });
            cmbQuality.SelectedIndex = 1;
            cmbQuality.SelectedIndexChanged += (s, e) =>
            {
                jpegQuality = cmbQuality.SelectedIndex == 0 ? 85L : (cmbQuality.SelectedIndex == 1 ? 70L : 60L);
            };

            lblTelemetry = new Label
            {
                Text = "Pressure: 0% | Pos: (0, 0) | Tilt: (0°, 0°)",
                Location = new Point(20, 220),
                Size = new Size(400, 25),
                Font = new Font("Consolas", 9),
                ForeColor = Color.LightGray
            };

            Controls.AddRange(new Control[] { lblStatus, lblIps, lblMonTitle, cmbMonitors, lblQualTitle, cmbQuality, lblTelemetry });

            penDevice = CreateSyntheticPointerDevice(PT_PEN, 1, 1);
            if (penDevice == IntPtr.Zero)
            {
                lblStatus.Text = "Windows Ink Error (Run as Administrator)";
                lblStatus.ForeColor = Color.Red;
                return;
            }

            _ = Task.Run(RunDiscoveryResponder);
            _ = Task.Run(RunPenListener);
            _ = Task.Run(RunScreenStreamer);
        }

        private string GetLocalIps()
        {
            var ips = NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni => ni.OperationalStatus == OperationalStatus.Up && ni.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(ni => ni.GetIPProperties().UnicastAddresses)
                .Where(ip => ip.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(ip => ip.Address.ToString());
            return string.Join(" | ", ips);
        }

        private async Task RunScreenStreamer()
        {
            TcpListener listener = new TcpListener(IPAddress.Any, 5002);
            listener.Start();

            ImageCodecInfo jpegCodec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);

            while (true)
            {
                TcpClient client = await listener.AcceptTcpClientAsync();
                client.NoDelay = true;
                client.SendBufferSize = 65536;

                _ = Task.Run(async () =>
                {
                    try
                    {
                        using NetworkStream stream = client.GetStream();
                        byte[] lenBuf = new byte[4];

                        while (client.Connected)
                        {
                            Rectangle bounds = activeBounds;
                            int targetW = jpegQuality >= 70 ? 1920 : 1440;
                            int targetH = jpegQuality >= 70 ? 1080 : 810;

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

                            using EncoderParameters encParams = new EncoderParameters(1);
                            encParams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, jpegQuality);

                            using MemoryStream ms = new MemoryStream();
                            scaledBmp.Save(ms, jpegCodec, encParams);
                            byte[] jpgBytes = ms.ToArray();

                            BinaryPrimitives.WriteInt32BigEndian(lenBuf, jpgBytes.Length);
                            await stream.WriteAsync(lenBuf, 0, 4);
                            await stream.WriteAsync(jpgBytes, 0, jpgBytes.Length);

                            await Task.Delay(28); // ~35 FPS
                        }
                    }
                    catch { client.Close(); }
                });
            }
        }

        private async Task RunPenListener()
        {
            using UdpClient udp = new UdpClient(5000);
            while (true)
            {
                var res = await udp.ReceiveAsync();
                byte[] d = res.Buffer;

                if (d.Length == 4)
                {
                    TriggerShortcut(BinaryPrimitives.ReadInt32BigEndian(d));
                    continue;
                }

                if (d.Length < 28) continue;

                int action = BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(0, 4));
                float normX = BinaryPrimitives.ReadSingleBigEndian(d.AsSpan(4, 4));
                float normY = BinaryPrimitives.ReadSingleBigEndian(d.AsSpan(8, 4));
                float pressure = BinaryPrimitives.ReadSingleBigEndian(d.AsSpan(12, 4));
                float tilt = BinaryPrimitives.ReadSingleBigEndian(d.AsSpan(16, 4));
                float orientation = BinaryPrimitives.ReadSingleBigEndian(d.AsSpan(20, 4));
                int isEraser = BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(24, 4));

                InjectPen(action, normX, normY, pressure, tilt, orientation, isEraser);
            }
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

            BeginInvoke(new Action(() =>
            {
                lblTelemetry.Text = $"Pressure: {(int)(pressure * 100)}% | Pos: ({px}, {py}) | Tilt: ({tiltX}°, {tiltY}°)";
            }));
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
            using UdpClient disc = new UdpClient(5001);
            byte[] ok = Encoding.UTF8.GetBytes("MATEPAD_INK_SERVER_OK");
            while (true)
            {
                var req = await disc.ReceiveAsync();
                if (Encoding.UTF8.GetString(req.Buffer) == "DISCOVER_MATEPAD_SERVER")
                {
                    await disc.SendAsync(ok, ok.Length, req.RemoteEndPoint);
                    BeginInvoke(new Action(() =>
                    {
                        lblStatus.Text = $"Connected to MatePad ({req.RemoteEndPoint.Address})";
                        lblStatus.ForeColor = Color.SpringGreen;
                    }));
                }
            }
        }
    }
}