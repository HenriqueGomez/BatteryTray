// BatteryTray - mostra a % de bateria dos dispositivos sem fio na barra de tarefas do Windows 11.
// Cada aparelho é um ícone de bandeja fixado; o texto (ícone + %) é desenhado por cima, dentro da barra.
// Fontes de leitura:
//  - Logitech (LIGHTSPEED/Unifying/Bolt ou cabo): HID++ 2.0 (features 0x1004, 0x1000, 0x1001, 0x1F20)
//  - Razer, headsets: eventos "Battery Status" dos logs do Razer Synapse 4
//  - Razer, mouses/teclados sem fio: feature report de 90 bytes (protocolo do openrazer)
//  - Bluetooth de qualquer marca: bateria guardada pelo Windows nas propriedades do dispositivo
//  - USB/HID genérico: campo de bateria padrão (Battery Strength / Remaining Capacity)
//  - Controles XInput que informam bateria
// Atualização: consulta o último Release do GitHub (veja Updater e release.ps1).
// Compilar com build.cmd (usa o csc do .NET Framework 4, que já vem no Windows). Licença MIT.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace BatteryTray
{
    public enum DeviceKind { Mouse, Headset, Keyboard, Controller, Other }

    public class Reading
    {
        public string Key;
        public string Name;
        public DeviceKind Kind;
        public int Percent = -1;      // -1 = desconhecido
        public string LevelText;      // para fontes que só dão faixa (XInput)
        public bool Charging;
        public bool Online;
        public DateTime Updated;
    }

    public interface IBatterySource { List<Reading> Poll(); }

    // ------------------------------------------------------------------ HID nativo
    static class Hid
    {
        [StructLayout(LayoutKind.Sequential)] struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid g; public int Flags; public IntPtr Reserved; }
        [StructLayout(LayoutKind.Sequential)] struct HIDP_CAPS { public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 27)] public ushort[] Rest; }
        [StructLayout(LayoutKind.Sequential)] struct HIDD_ATTRIBUTES { public int Size; public ushort VendorID, ProductID, VersionNumber; }

        [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid g);
        [DllImport("hid.dll")] static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES a);
        [DllImport("hid.dll")] static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr p);
        [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr p);
        [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr p, out HIDP_CAPS c);
        [DllImport("hid.dll")] static extern bool HidD_GetProductString(SafeFileHandle h, byte[] b, int len);
        [DllImport("hid.dll")] static extern bool HidD_GetManufacturerString(SafeFileHandle h, byte[] b, int len);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr e, IntPtr hwnd, int flags);
        [DllImport("setupapi.dll")] static extern bool SetupDiEnumDeviceInterfaces(IntPtr s, IntPtr d, ref Guid g, int i, ref SP_DEVICE_INTERFACE_DATA did);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr s, ref SP_DEVICE_INTERFACE_DATA did, IntPtr detail, int size, out int req, IntPtr dev);
        [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr s);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern SafeFileHandle CreateFile(string n, uint access, uint share, IntPtr sa, uint disp, uint flags, IntPtr t);

        [DllImport("hid.dll")] public static extern bool HidD_SetFeature(SafeFileHandle h, byte[] b, int len);
        [DllImport("hid.dll")] public static extern bool HidD_GetFeature(SafeFileHandle h, byte[] b, int len);
        [DllImport("hid.dll")] public static extern bool HidD_GetInputReport(SafeFileHandle h, byte[] b, int len);

        // HIDP_VALUE_CAPS (72 bytes)
        [StructLayout(LayoutKind.Sequential)]
        public struct ValueCaps
        {
            public ushort UsagePage; public byte ReportID; public byte IsAlias; public ushort BitField, LinkCollection, LinkUsage, LinkUsagePage;
            public byte IsRange, IsStringRange, IsDesignatorRange, IsAbsolute, HasNull, Reserved;
            public ushort BitSize, ReportCount, R1, R2, R3, R4, R5;
            public uint UnitsExp, Units; public int LogicalMin, LogicalMax, PhysicalMin, PhysicalMax;
            public ushort UsageMin, UsageMax, S1, S2, D1, D2, I1, I2; // Usage = UsageMin quando IsRange == 0
        }
        [DllImport("hid.dll")] static extern int HidP_GetValueCaps(int reportType, [Out] ValueCaps[] caps, ref ushort len, IntPtr preparsed);
        [DllImport("hid.dll")] static extern int HidP_GetUsageValue(int reportType, ushort usagePage, ushort link, ushort usage, out uint value, IntPtr preparsed, byte[] report, int len);
        const int HIDP_STATUS_SUCCESS = 0x00110000;

        public class Info { public string Path; public ushort Vid, Pid, UsagePage, Usage, InLen, OutLen, FeatLen; public string Product; }

        // Envia um relatório de saída e espera, por até timeoutMs, um relatório de entrada aceito por "accept".
        // (Para aparelhos que só conversam por relatórios de entrada/saída, sem "feature reports".)
        // Para o diagnóstico: o último quadro aceito pela última chamada de Exchange nesta thread, ou o motivo da falha.
        [ThreadStatic] public static byte[] LastFrame;
        [ThreadStatic] public static string LastError;

        public static byte[] Exchange(Info info, byte[] request, Func<byte[], bool> accept, int timeoutMs)
        {
            LastFrame = null; LastError = null;
            var h = CreateFile(info.Path, 0xC0000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero); // leitura/escrita, overlapped
            bool readOnly = false;
            if (h.IsInvalid && request == null) { h.Dispose(); h = CreateFile(info.Path, 0x80000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero); readOnly = true; }
            if (h.IsInvalid) { LastError = "não abriu (erro " + Marshal.GetLastWin32Error() + ")"; h.Dispose(); return null; }
            int others = 0; string firstOther = null;
            using (var fs = new FileStream(h, readOnly ? FileAccess.Read : FileAccess.ReadWrite, 1, true))
            {
                if (request != null) // null: só escuta (aparelhos que enviam a bateria sozinhos)
                {
                    int outLen = info.OutLen > 0 ? info.OutLen : request.Length;
                    var req = new byte[outLen];
                    Array.Copy(request, req, Math.Min(request.Length, outLen));
                    try { fs.Write(req, 0, outLen); fs.Flush(); }
                    catch (IOException ex) { LastError = "o envio falhou (relatório de saída: " + info.OutLen + " B; " + ex.Message.Trim() + ")"; return null; }
                }
                var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                int inLen = Math.Max(1, (int)info.InLen);
                while (true)
                {
                    int left = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                    if (left > 0)
                    {
                        var buf = new byte[inLen];
                        IAsyncResult ar;
                        try { ar = fs.BeginRead(buf, 0, inLen, null, null); }
                        catch (IOException ex) { LastError = "a leitura falhou (" + ex.Message.Trim() + ")"; return null; }
                        if (ar.AsyncWaitHandle.WaitOne(left)) // fechar o arquivo cancela a leitura pendente
                        {
                            int n;
                            try { n = fs.EndRead(ar); }
                            catch (IOException ex) { LastError = "a leitura falhou (" + ex.Message.Trim() + ")"; return null; }
                            if (n > 0 && accept(buf)) { LastFrame = buf; return buf; }
                            if (n > 0 && others++ == 0) firstOther = BitConverter.ToString(buf, 0, Math.Min(n, 16)).Replace('-', ' ');
                            continue;
                        }
                    }
                    LastError = "sem resposta em " + timeoutMs + " ms" + (request != null ? " (relatório de saída: " + info.OutLen + " B)" : "")
                              + (others > 0 ? "; chegaram " + others + " quadro(s) de outro tipo, o primeiro: " + firstOther : "");
                    return null;
                }
            }
        }

        // Leitura/escrita; se o Windows não deixar (teclados e mouses são exclusivos do sistema), abre só para consulta.
        public static SafeFileHandle Open(string path)
        {
            var h = CreateFile(path, 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (!h.IsInvalid) return h;
            h.Dispose();
            return CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        }

        // Procura um campo de bateria padrão HID e devolve a % (ou -1):
        //  Generic Device Controls (0x06) / Battery Strength (0x20)  ·  Battery System (0x85) / RemainingCapacity (0x66)
        public static int ReadStandardBattery(Info info) { bool f; return ReadStandardBattery(info, out f); }

        // hasField: o aparelho declara um campo de bateria (mesmo que a leitura tenha falhado)
        public static int ReadStandardBattery(Info info, out bool hasField)
        {
            hasField = false;
            using (var h = Open(info.Path))
            {
                if (h.IsInvalid) return -1;
                IntPtr pp;
                if (!HidD_GetPreparsedData(h, out pp)) return -1;
                try
                {
                    foreach (int type in new[] { 2, 0 }) // feature, depois input
                    {
                        int repLen = type == 2 ? info.FeatLen : info.InLen;
                        if (repLen == 0) continue;
                        var caps = new ValueCaps[256]; ushort n = (ushort)caps.Length;
                        if (HidP_GetValueCaps(type, caps, ref n, pp) != HIDP_STATUS_SUCCESS) continue;
                        for (int i = 0; i < n; i++)
                        {
                            var c = caps[i];
                            ushort usage = c.UsageMin;
                            bool battery = (c.UsagePage == 0x06 && usage == 0x20) || (c.UsagePage == 0x85 && usage == 0x66);
                            if (!battery || c.IsRange != 0) continue;
                            hasField = true;
                            var rep = new byte[repLen]; rep[0] = c.ReportID;
                            bool ok = type == 2 ? HidD_GetFeature(h, rep, rep.Length) : HidD_GetInputReport(h, rep, rep.Length);
                            if (!ok) continue;
                            uint v;
                            if (HidP_GetUsageValue(type, c.UsagePage, c.LinkCollection, usage, out v, pp, rep, rep.Length) != HIDP_STATUS_SUCCESS) continue;
                            int min = c.LogicalMin, max = c.LogicalMax;
                            if (max <= min) { min = 0; max = c.UsagePage == 0x85 ? 100 : 255; }
                            if (max == 100 && min == 0 && v <= 100) return (int)v;
                            return (int)Math.Round(Math.Max(0, Math.Min(1, ((int)v - min) / (double)(max - min))) * 100);
                        }
                    }
                }
                finally { HidD_FreePreparsedData(pp); }
            }
            return -1;
        }

        public static string Manufacturer(string path)
        {
            using (var h = CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero))
            {
                var b = new byte[256];
                if (h.IsInvalid || !HidD_GetManufacturerString(h, b, b.Length)) return null;
                return Encoding.Unicode.GetString(b).TrimEnd('\0');
            }
        }

        // Por onde o aparelho está ligado, pelo caminho do Windows (sem números de série nem endereços).
        public static string Bus(string path)
        {
            string p = path.ToLowerInvariant();
            if (p.Contains("{00001124-0000-1000-8000-00805f9b34fb}")) return "Bluetooth";
            if (p.Contains("{00001812-0000-1000-8000-00805f9b34fb}")) return "Bluetooth LE";
            if (p.StartsWith(@"\\?\hid#vid_")) return "USB";
            return "outro";
        }

        // A lista é montada uma vez a cada poucos segundos e compartilhada pelas fontes de uma mesma rodada:
        // montar abre todos os aparelhos HID do PC, e um aparelho lento atrasaria cada fonte.
        static readonly object enumLock = new object();
        static List<Info> enumCache;
        static DateTime enumAt;

        public static List<Info> Enumerate()
        {
            lock (enumLock)
            {
                if (enumCache == null || (DateTime.UtcNow - enumAt).TotalSeconds > 5)
                {
                    enumCache = EnumerateNow();
                    enumAt = DateTime.UtcNow;
                }
                return new List<Info>(enumCache);
            }
        }

        static List<Info> EnumerateNow()
        {
            var res = new List<Info>();
            Guid g; HidD_GetHidGuid(out g);
            IntPtr set = SetupDiGetClassDevs(ref g, IntPtr.Zero, IntPtr.Zero, 0x12);
            var did = new SP_DEVICE_INTERFACE_DATA(); did.cbSize = Marshal.SizeOf(did);
            for (int i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref g, i, ref did); i++)
            {
                int req; SetupDiGetDeviceInterfaceDetail(set, ref did, IntPtr.Zero, 0, out req, IntPtr.Zero);
                IntPtr buf = Marshal.AllocHGlobal(req);
                Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                string path = null;
                if (SetupDiGetDeviceInterfaceDetail(set, ref did, buf, req, out req, IntPtr.Zero)) path = Marshal.PtrToStringUni(buf + 4);
                Marshal.FreeHGlobal(buf);
                if (path == null) continue;
                using (var h = CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero))
                {
                    if (h.IsInvalid) continue;
                    var a = new HIDD_ATTRIBUTES(); a.Size = Marshal.SizeOf(a);
                    if (!HidD_GetAttributes(h, ref a)) continue;
                    IntPtr p; var c = new HIDP_CAPS();
                    if (HidD_GetPreparsedData(h, out p)) { HidP_GetCaps(p, out c); HidD_FreePreparsedData(p); }
                    var pb = new byte[256]; string prod = null;
                    if (HidD_GetProductString(h, pb, pb.Length)) prod = Encoding.Unicode.GetString(pb).TrimEnd('\0');
                    res.Add(new Info { Path = path, Vid = a.VendorID, Pid = a.ProductID, UsagePage = c.UsagePage, Usage = c.Usage,
                                       InLen = c.InputReportByteLength, OutLen = c.OutputReportByteLength, FeatLen = c.FeatureReportByteLength, Product = prod });
                }
            }
            SetupDiDestroyDeviceInfoList(set);
            return res;
        }
    }

    // ------------------------------------------------------------------ Logitech HID++
    class HppChannel : IDisposable
    {
        // "software id" muda a cada pedido (0x8..0xF): uma resposta atrasada de um pedido anterior
        // (ex.: mouse acordando) não é confundida com a do pedido atual, nem com as do G HUB.
        static int swCounter;
        readonly FileStream fs;
        readonly BlockingCollection<byte[]> inbox = new BlockingCollection<byte[]>();
        volatile bool closed;

        public HppChannel(string path)
        {
            var h = Hid.CreateFile(path, 0xC0000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
            if (h.IsInvalid) throw new IOException("open failed");
            fs = new FileStream(h, FileAccess.ReadWrite, 1, true); // sem buffer interno
            BeginRead();
        }

        void BeginRead()
        {
            var buf = new byte[20];
            try
            {
                fs.BeginRead(buf, 0, 20, ar =>
                {
                    try { int n = fs.EndRead(ar); if (n > 0) inbox.Add(buf); }
                    catch { closed = true; return; }
                    if (!closed) BeginRead();
                }, null);
            }
            catch { closed = true; }
        }

        // Retorna os bytes de parâmetro da resposta (a partir do índice 4) ou null.
        public byte[] Call(byte dev, byte featIdx, byte fn, int timeoutMs, params byte[] args)
        {
            byte[] dummy;
            while (inbox.TryTake(out dummy)) { }
            var r = new byte[20]; r[0] = 0x11; r[1] = dev; r[2] = featIdx; r[3] = (byte)((fn << 4) | (0x8 + (Interlocked.Increment(ref swCounter) & 7)));
            for (int i = 0; i < args.Length && i < 16; i++) r[4 + i] = args[i];
            fs.Write(r, 0, 20);
            fs.Flush();
            Log("-> " + Hex(r));
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (true)
            {
                int left = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                if (left <= 0) { Log("   timeout"); return null; }
                byte[] a;
                if (!inbox.TryTake(out a, left)) { Log("   timeout"); return null; }
                Log("<- " + Hex(a));
                if (a[0] != 0x11 || a[1] != dev) continue;
                if (a[2] == 0xFF && a[3] == featIdx && a[4] == r[3]) return null; // erro HID++ 2.0
                if (a[2] == featIdx && a[3] == r[3]) { var p = new byte[16]; Array.Copy(a, 4, p, 0, 16); return p; }
            }
        }

        public void Dispose() { closed = true; try { fs.Dispose(); } catch { } }

        public static string DebugLog; // caminho do log (só no --dump e durante o diagnóstico)
        static void Log(string s) { if (DebugLog != null) File.AppendAllText(DebugLog, DateTime.Now.ToString("HH:mm:ss.fff ") + s + Environment.NewLine); }
        static string Hex(byte[] b) { var sb = new StringBuilder(); foreach (var x in b) sb.AppendFormat("{0:X2} ", x); return sb.ToString(); }
    }

    class LogitechSource : IBatterySource
    {
        class Known { public string Path; public byte Dev; public byte BattIdx; public ushort BattFeature; public string Name; public DeviceKind Kind; public Reading Last; }
        readonly Dictionary<string, Known> known = new Dictionary<string, Known>();
        DateTime lastScan = DateTime.MinValue;

        public List<Reading> Poll()
        {
            // canal HID++ "longo": receptores/aparelhos comuns (FF00/0002) e aparelhos G mais novos, como headsets (FF43/0202)
            var all = Hid.Enumerate();
            var collections = all.Where(i => i.Vid == 0x046D &&
                ((i.UsagePage == 0xFF00 && i.Usage == 0x0002) || (i.UsagePage == 0xFF43 && i.Usage == 0x0202))).ToList();
            bool rescan = (DateTime.UtcNow - lastScan).TotalMinutes >= 5;
            var paths = new HashSet<string>(collections.Select(c => c.Path));

            foreach (var col in collections)
            {
                var devs = new List<byte>();
                bool anyKnown = known.Values.Any(k => k.Path == col.Path);
                if (rescan || !anyKnown)
                {
                    var shortCol = ShortCollection(all, col);
                    var slots = new byte[] { 1, 2, 3, 4, 5, 6 };
                    var paired = slots.Where(d => !EmptySlot(shortCol, d)).ToList();
                    devs.AddRange(paired);
                    // 0xFF é o aparelho ligado direto (cabo, headset); num receptor (alguma posição vazia) é o próprio receptor
                    if (paired.Count == slots.Length) devs.Add(0xFF);
                }
                else devs.AddRange(known.Values.Where(k => k.Path == col.Path).Select(k => k.Dev));

                HppChannel ch;
                try { ch = new HppChannel(col.Path); } catch { continue; }
                using (ch)
                {

                    foreach (byte dev in devs.Distinct())
                    {
                        // chave pelo modelo do receptor e o número do aparelho nele, sem o caminho USB (que muda ao trocar
                        // de porta); só com dois receptores iguais ligados o caminho entra, para não misturar os dois
                        bool twin = collections.Count(c => c.Pid == col.Pid) > 1;
                        string key = twin ? "logi:" + col.Path + "#" + dev : "logi:" + col.Pid.ToString("X4") + "#" + dev;
                        Known k, old;
                        known.TryGetValue(key, out old);
                        k = old;
                        // redescobre na varredura ou se a identificação anterior ficou incompleta
                        if (old == null || rescan || old.Kind == DeviceKind.Other)
                        {
                            var fresh = Discover(ch, col.Path, dev);
                            if (fresh != null) { fresh.Last = old != null ? old.Last : null; k = fresh; }
                        }
                        if (k == null) continue;
                        known[key] = k;
                        if (k.Last == null) k.Last = new Reading { Key = key };
                        k.Last.Name = k.Name; k.Last.Kind = k.Kind;
                        ReadBattery(ch, k);
                    }
                }
            }
            if (rescan) lastScan = DateTime.UtcNow;
            foreach (var k in known.Values.Where(k => !paths.Contains(k.Path))) k.Last.Online = false;
            return known.Values.Select(k => k.Last).ToList();
        }

        // Canal HID++ "curto" (FF00/0001) do mesmo receptor: é por ele que o receptor responde os erros.
        static Hid.Info ShortCollection(List<Hid.Info> all, Hid.Info longCol)
        {
            // \\?\hid#vid_046d&pid_c547&mi_02&col02#a&2ee94837&0&0001#{...}: tira o "&colNN" e o último número da instância
            Func<string, string> baseOf = p =>
            {
                var parts = p.Split('#');
                if (parts.Length < 3) return p;
                return Regex.Replace(parts[1], "&col[0-9a-f]{2}", "", RegexOptions.IgnoreCase) + "#" + Regex.Replace(parts[2], "&[0-9a-f]+$", "", RegexOptions.IgnoreCase);
            };
            string b = baseOf(longCol.Path);
            return all.FirstOrDefault(i => i.Vid == longCol.Vid && i.Pid == longCol.Pid && i.UsagePage == 0xFF00 && i.Usage == 0x0001
                                           && string.Equals(baseOf(i.Path), b, StringComparison.OrdinalIgnoreCase));
        }

        // Posição vazia do receptor: um "ping" curto volta na hora com o erro 0x08 (aparelho desconhecido).
        // Assim a varredura não espera 1,5 s em cada uma das 6 posições. Aparelho pareado mas dormindo não dá esse erro.
        static bool EmptySlot(Hid.Info shortCol, byte dev)
        {
            if (shortCol == null) return false; // aparelho no cabo, sem canal curto: pergunta normalmente
            var r = Hid.Exchange(shortCol, new byte[] { 0x10, dev, 0x00, 0x1A, 0x00, 0x00, 0x00 },
                                 f => f.Length >= 6 && f[0] == 0x10 && f[1] == dev && f[2] == 0x8F, 150);
            return r != null && r[5] == 0x08;
        }

        // -1 = aparelho não respondeu; 0 = não tem a feature; >0 = índice da feature
        static int FeatureIndexRaw(HppChannel ch, byte dev, ushort feature, int timeout)
        {
            var p = ch.Call(dev, 0x00, 0, timeout, (byte)(feature >> 8), (byte)feature);
            return p == null ? -1 : p[0];
        }

        static byte FeatureIndex(HppChannel ch, byte dev, ushort feature, int timeout)
        {
            return (byte)Math.Max(0, FeatureIndexRaw(ch, dev, feature, timeout));
        }

        // Features de bateria, na ordem de preferência:
        //  0x1004 UNIFIED_BATTERY (% exata) · 0x1000 BATTERY_STATUS (%) ·
        //  0x1001 BATTERY_VOLTAGE (mV -> % estimada) · 0x1F20 ADC_MEASUREMENT (headsets G; mV -> % estimada)
        static readonly ushort[] BatteryFeatures = { 0x1004, 0x1000, 0x1001, 0x1F20 };

        static Known Discover(HppChannel ch, string path, byte dev)
        {
            byte idx = 0; ushort feat = 0;
            for (int i = 0; i < BatteryFeatures.Length && idx == 0; i++)
            {
                int r = FeatureIndexRaw(ch, dev, BatteryFeatures[i], i == 0 ? 1500 : 500); // 1ª pergunta: tempo para acordar
                if (r < 0 && i == 0) return null; // nada responde neste índice: não há aparelho
                if (r > 0) { idx = (byte)r; feat = BatteryFeatures[i]; }
            }
            if (idx == 0) return null;
            var k = new Known { Path = path, Dev = dev, BattIdx = idx, BattFeature = feat, Name = "Logitech", Kind = DeviceKind.Other };
            byte nameIdx = FeatureIndex(ch, dev, 0x0005, 500);
            if (nameIdx != 0)
            {
                var c = ch.Call(dev, nameIdx, 0, 500);
                if (c != null)
                {
                    var sb = new StringBuilder();
                    for (int off = 0; off < c[0]; off += 16)
                    {
                        var part = ch.Call(dev, nameIdx, 1, 500, (byte)off);
                        if (part == null) break;
                        for (int i = 0; i < 16 && sb.Length < c[0]; i++) sb.Append((char)part[i]);
                    }
                    if (sb.Length > 0) k.Name = sb.ToString().TrimEnd('\0');
                }
                var t = ch.Call(dev, nameIdx, 2, 500);
                if (t != null)
                {
                    // tipos do HID++ (feature 0x0005): 0 teclado, 1 controle remoto, 2 teclado numérico, 3 mouse,
                    // 4 touchpad, 5 trackball, 6 apresentador, 7 receptor, 8 headset, 9 webcam, 10 volante,
                    // 11 joystick, 12 gamepad, 13 dock, 14 caixa de som, 15 microfone
                    switch (t[0])
                    {
                        case 0: case 2: k.Kind = DeviceKind.Keyboard; break;
                        case 3: case 4: case 5: k.Kind = DeviceKind.Mouse; break;
                        case 8: k.Kind = DeviceKind.Headset; break;
                        case 10: case 11: case 12: k.Kind = DeviceKind.Controller; break;
                    }
                }
            }
            return k;
        }

        // Curva tensão (mV) -> % de bateria de íon-lítio usada pelo Solaar para estes aparelhos.
        static readonly int[,] VoltageCurve = {
            { 4186, 100 }, { 4067, 90 }, { 3989, 80 }, { 3922, 70 }, { 3859, 60 }, { 3811, 50 },
            { 3778, 40 }, { 3751, 30 }, { 3717, 20 }, { 3671, 10 }, { 3646, 5 }, { 3579, 2 }, { 3500, 0 } };

        static int PercentFromMillivolts(int mv)
        {
            if (mv >= VoltageCurve[0, 0]) return 100;
            for (int i = 1; i < VoltageCurve.GetLength(0); i++)
            {
                int v1 = VoltageCurve[i, 0], p1 = VoltageCurve[i, 1], v0 = VoltageCurve[i - 1, 0], p0 = VoltageCurve[i - 1, 1];
                if (mv >= v1) return p1 + (int)Math.Round((mv - v1) * (p0 - p1) / (double)(v0 - v1));
            }
            return 0;
        }

        static void ReadBattery(HppChannel ch, Known k)
        {
            var p = k.BattFeature == 0x1004 ? ch.Call(k.Dev, k.BattIdx, 1, 1500) : ch.Call(k.Dev, k.BattIdx, 0, 1500);
            if (p == null) { k.Last.Online = false; return; }   // dispositivo dormindo/desligado
            k.Last.Online = true;
            k.Last.Updated = DateTime.Now;
            if (k.BattFeature == 0x1001)
            {
                // [tensão mV (2 bytes, big-endian)] [flags: bit7 = carregando; bits0-2 = 1 -> carga completa]
                int mv = (p[0] << 8) | p[1];
                bool plugged = (p[2] & 0x80) != 0;
                k.Last.Percent = PercentFromMillivolts(mv);
                k.Last.Charging = plugged && (p[2] & 0x07) == 0;
                if (plugged && (p[2] & 0x07) == 1) { k.Last.Percent = 100; k.Last.Charging = true; }
                k.Last.LevelText = "estimada pela tensão";
                return;
            }
            if (k.BattFeature == 0x1F20)
            {
                // [tensão mV (2 bytes)] [flags: bit0 = ligado, bit1 = carregando]
                if ((p[2] & 0x01) == 0) { k.Last.Online = false; return; } // headset desligado (dongle responde)
                k.Last.Percent = PercentFromMillivolts((p[0] << 8) | p[1]);
                k.Last.Charging = (p[2] & 0x02) != 0;
                k.Last.LevelText = "estimada pela tensão";
                return;
            }
            if (k.BattFeature == 0x1004)
            {
                int pct = p[0];
                if (pct == 0) // sem % exata: usa a faixa
                {
                    int lvl = p[1];
                    pct = (lvl & 8) != 0 ? 100 : (lvl & 4) != 0 ? 60 : (lvl & 2) != 0 ? 20 : (lvl & 1) != 0 ? 5 : -1;
                }
                k.Last.Percent = pct;
                k.Last.Charging = p[2] == 1 || p[2] == 2;
                if (p[2] == 3) { k.Last.Percent = 100; k.Last.Charging = true; }
            }
            else
            {
                k.Last.Percent = p[0];
                k.Last.Charging = p[2] >= 1 && p[2] <= 4;
            }
        }
    }

    // ------------------------------------------------------------------ Razer (logs do Synapse 4)
    class RazerLogSource : IBatterySource
    {
        static readonly Regex Evt = new Regex("^\\[(?<ts>[0-9/]+ [0-9:.]+)\\].*?_onEvent\\(\\), dataFired:\\{\"productId\":(?<pid>\\d+).*?\"eventName\":\"(?<ev>Battery Status|Link Status)\"(?<rest>.*)$", RegexOptions.Compiled);
        static readonly Regex Level = new Regex("\"level\":(\\d+)", RegexOptions.Compiled);
        static readonly Regex Status = new Regex("\"status\":(\\d+)", RegexOptions.Compiled);
        static readonly Regex StateEnum = new Regex("\"stateEnum\":\"([^\"]+)\"", RegexOptions.Compiled);

        class Dev { public string LevelTs = "", LinkTs = ""; public int Level = -1; public bool Linked; public bool Charging; }
        readonly Dictionary<string, Dev> devs = new Dictionary<string, Dev>();
        readonly Dictionary<string, long> offsets = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        readonly string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Razer\RazerAppEngine\User Data\Logs");
        readonly Dictionary<string, string> names = new Dictionary<string, string>();

        public List<Reading> Poll()
        {
            var result = new List<Reading>();
            if (!Directory.Exists(dir)) return result;
            var files = new DirectoryInfo(dir).GetFiles("lighting-engine*.log").OrderBy(f => f.LastWriteTimeUtc).ToList();
            foreach (var f in files) ReadNew(f);

            bool synapse = System.Diagnostics.Process.GetProcessesByName("RazerAppEngine").Length > 0;
            HashSet<int> present = null;
            foreach (var kv in devs)
            {
                int pid = int.Parse(kv.Key);
                if (present == null) present = new HashSet<int>(Hid.Enumerate().Where(i => i.Vid == 0x1532).Select(i => (int)i.Pid));
                string name;
                if (!names.TryGetValue(kv.Key, out name))
                {
                    var hid = Hid.Enumerate().FirstOrDefault(i => i.Vid == 0x1532 && i.Pid == pid && !string.IsNullOrEmpty(i.Product));
                    name = hid != null ? hid.Product : "Razer";
                    if (hid != null) names[kv.Key] = name;
                }
                var d = kv.Value;
                result.Add(new Reading
                {
                    Key = "razer:" + kv.Key,
                    Name = name,
                    Kind = DeviceKind.Headset,
                    Percent = d.Level,
                    Charging = d.Charging,
                    Online = synapse && d.Linked && present.Contains(pid),
                    Updated = ParseTs(d.LevelTs)
                });
            }
            return result;
        }

        static DateTime ParseTs(string ts)
        {
            DateTime t;
            return DateTime.TryParseExact(ts, "yyyy/MM/dd HH:mm:ss.fff", null, System.Globalization.DateTimeStyles.None, out t) ? t : DateTime.MinValue;
        }

        void ReadNew(FileInfo f)
        {
            long off;
            offsets.TryGetValue(f.FullName, out off);
            if (f.Length < off) off = 0; // arquivo rotacionado
            if (f.Length == off) return;
            try
            {
                using (var s = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    s.Seek(off, SeekOrigin.Begin);
                    var bytes = new byte[s.Length - off];
                    int read = 0;
                    while (read < bytes.Length) { int n = s.Read(bytes, read, bytes.Length - read); if (n <= 0) break; read += n; }
                    int lastNl = Array.LastIndexOf(bytes, (byte)'\n', read - 1 < 0 ? 0 : read - 1);
                    if (lastNl < 0) return; // linha incompleta, tenta depois
                    var text = Encoding.UTF8.GetString(bytes, 0, lastNl + 1);
                    offsets[f.FullName] = off + lastNl + 1;
                    foreach (var line in text.Split('\n'))
                    {
                        if (line.IndexOf("dataFired:", StringComparison.Ordinal) < 0) continue;
                        var m = Evt.Match(line);
                        if (!m.Success) continue;
                        string pid = m.Groups["pid"].Value, ts = m.Groups["ts"].Value, rest = m.Groups["rest"].Value;
                        Dev d;
                        if (!devs.TryGetValue(pid, out d)) devs[pid] = d = new Dev();
                        if (m.Groups["ev"].Value == "Battery Status")
                        {
                            var lm = Level.Match(rest);
                            if (lm.Success && string.CompareOrdinal(ts, d.LevelTs) >= 0)
                            {
                                d.LevelTs = ts; d.Level = int.Parse(lm.Groups[1].Value);
                                var se = StateEnum.Match(rest);
                                d.Charging = se.Success && se.Groups[1].Value.IndexOf("CHARG", StringComparison.OrdinalIgnoreCase) >= 0;
                            }
                        }
                        else
                        {
                            var sm = Status.Match(rest);
                            if (sm.Success && string.CompareOrdinal(ts, d.LinkTs) >= 0) { d.LinkTs = ts; d.Linked = sm.Groups[1].Value != "0"; }
                        }
                    }
                }
            }
            catch (IOException) { }
        }
    }

    // ------------------------------------------------------------------ Tipo do aparelho (nome, função HID, classe Bluetooth)
    static class DeviceClassifier
    {
        static readonly string[][] Words = {
            new[] { "Headset", "headset", "headphone", "kraken", "blackshark", "barracuda", "nari", "hammerhead", "buds", "earbud", "fone", "airpods", "audio" },
            new[] { "Keyboard", "keyboard", "teclado", "blackwidow", "huntsman", "ornata", "cynosa", "deathstalker", "keypad" },
            new[] { "Controller", "controller", "gamepad", "joystick", "wolverine", "raiju", "kishi", "controle", "xbox", "dualsense", "dualshock" },
            new[] { "Mouse", "mouse", "viper", "basilisk", "deathadder", "naga", "orochi", "cobra", "trackball" },
        };

        public static DeviceKind FromName(string name)
        {
            if (string.IsNullOrEmpty(name)) return DeviceKind.Other;
            string n = name.ToLowerInvariant();
            if (Regex.IsMatch(n, @"\b(led|rgb|aura|lighting)\b.*controller")) return DeviceKind.Other; // controle de iluminação, não de jogo
            foreach (var w in Words)
                if (w.Skip(1).Any(x => n.Contains(x))) return (DeviceKind)Enum.Parse(typeof(DeviceKind), w[0]);
            return DeviceKind.Other;
        }

        // Funções declaradas pelas coleções HID do aparelho (Generic Desktop: 2 mouse, 6 teclado, 4/5 joystick/gamepad).
        // Vale a primeira (interface e coleção de menor número): é a função principal. Teclados costumam ter também
        // coleções de mouse ou joystick (teclas analógicas), e receptores de mouse, uma de teclado.
        public static DeviceKind FromHid(IEnumerable<Hid.Info> cols)
        {
            // Receptor sem fio com uma interface só de mouse: é o receptor de um mouse, mesmo que a primeira
            // interface seja de teclado (ex.: receptores "2.4G 8K" de mouses gamer)
            bool receiver = cols.Any(c => c.Product != null && Regex.IsMatch(c.Product, "receiver|dongle", RegexOptions.IgnoreCase));
            if (receiver && cols.Any(c => c.UsagePage == 1 && c.Usage == 2 && Regex.IsMatch(c.Path, "&mi_[0-9a-f]{2}#", RegexOptions.IgnoreCase)))
                return DeviceKind.Mouse;
            foreach (var c in cols.Where(c => c.UsagePage == 1).OrderBy(c => c.Path, StringComparer.OrdinalIgnoreCase))
            {
                if (c.Usage == 4 || c.Usage == 5) return DeviceKind.Controller;
                if (c.Usage == 2) return DeviceKind.Mouse;
                if (c.Usage == 6) return DeviceKind.Keyboard;
            }
            if (cols.Any(c => c.UsagePage == 0x0B)) return DeviceKind.Headset; // Telephony (botões de headset)
            return DeviceKind.Other;
        }

        // Class of Device do Bluetooth clássico: classe principal 4 = áudio, 5 = periférico (teclado/mouse/gamepad).
        public static DeviceKind FromCod(uint cod)
        {
            uint major = (cod >> 8) & 0x1F, minor = (cod >> 2) & 0x3F;
            if (major == 4) return DeviceKind.Headset;
            if (major == 5)
            {
                uint kb = (minor >> 4) & 3, sub = minor & 0xF;
                if (sub == 1 || sub == 2) return DeviceKind.Controller;
                if (kb == 2) return DeviceKind.Mouse;
                if (kb == 1 || kb == 3) return DeviceKind.Keyboard;
            }
            return DeviceKind.Other;
        }

        public static DeviceKind Best(params DeviceKind[] kinds)
        {
            foreach (var k in kinds) if (k != DeviceKind.Other) return k;
            return DeviceKind.Other;
        }
    }

    // ------------------------------------------------------------------ Razer sem fio (mouses/teclados) direto pelo USB
    // Protocolo de "feature report" de 90 bytes documentado pelo projeto openrazer:
    // classe 0x07 / comando 0x80 = nível de bateria (0-255), 0x84 = carregando.
    class RazerHidSource : IBatterySource
    {
        readonly Dictionary<string, Reading> last = new Dictionary<string, Reading>();
        readonly Dictionary<ushort, DateTime> skipUntil = new Dictionary<ushort, DateTime>();

        public List<Reading> Poll()
        {
            var all = Hid.Enumerate().Where(i => i.Vid == 0x1532).ToList();
            var seen = new HashSet<string>();
            foreach (var grp in all.Where(i => i.FeatLen == 91).GroupBy(i => i.Pid))
            {
                ushort pid = grp.Key;
                DateTime until;
                if (skipUntil.TryGetValue(pid, out until) && DateTime.UtcNow < until) continue;
                string key = "razerhid:" + pid.ToString("X4");
                int level = -1; bool charging = false, known = false;
                foreach (var info in grp)
                {
                    int st;
                    var lv = Query(info.Path, 0x07, 0x80, out st);
                    if (st == 2 && lv != null)
                    {
                        level = lv[1]; known = true;
                        var ch = Query(info.Path, 0x07, 0x84, out st);
                        charging = st == 2 && ch != null && ch[1] == 1;
                        break;
                    }
                    if (st == 4 && last.ContainsKey(key)) known = true; // timeout: aparelho desligado/dormindo
                }
                if (!known) { skipUntil[pid] = DateTime.UtcNow.AddMinutes(30); continue; } // sem bateria (ex.: com fio)
                Reading r;
                if (!last.TryGetValue(key, out r))
                {
                    string name = grp.Select(i => i.Product).FirstOrDefault(p => !string.IsNullOrEmpty(p)) ?? "Razer";
                    var cols = all.Where(i => i.Pid == pid);
                    last[key] = r = new Reading { Key = key, Name = name, Kind = DeviceClassifier.Best(DeviceClassifier.FromName(name), DeviceClassifier.FromHid(cols)) };
                }
                if (level >= 0) { r.Percent = (int)Math.Round(level * 100 / 255.0); r.Charging = charging; r.Online = true; r.Updated = DateTime.Now; }
                else r.Online = false;
                seen.Add(key);
            }
            foreach (var r in last.Values) if (!seen.Contains(r.Key)) r.Online = false;
            return last.Values.ToList();
        }

        // Retorna os argumentos da resposta (80 bytes) ou null. status: 2 ok, 1 ocupado, 3 falha, 4 timeout, 5 não suportado.
        static byte[] Query(string path, byte cls, byte id, out int status)
        {
            status = -1;
            using (var h = Hid.Open(path))
            {
                if (h.IsInvalid) return null;
                foreach (byte tid in new byte[] { 0x1F, 0xFF, 0x3F }) // "transaction id" varia por modelo
                {
                    var rep = new byte[91]; // [0] = report id 0, depois os 90 bytes do relatório
                    rep[2] = tid; rep[6] = 0x02; rep[7] = cls; rep[8] = id;
                    byte crc = 0; for (int i = 3; i < 89; i++) crc ^= rep[i];
                    rep[89] = crc;
                    if (!Hid.HidD_SetFeature(h, rep, rep.Length)) continue;
                    for (int attempt = 0; attempt < 5; attempt++)
                    {
                        Thread.Sleep(attempt == 0 ? 25 : 50);
                        var resp = new byte[91];
                        if (!Hid.HidD_GetFeature(h, resp, resp.Length)) break;
                        if (resp[7] != cls || resp[8] != id) continue; // resposta de outro pedido (ex.: Synapse)
                        status = resp[1];
                        if (status == 1) continue;                     // ocupado: espera mais
                        if (status == 2) { var a = new byte[80]; Array.Copy(resp, 9, a, 0, 80); return a; }
                        break;
                    }
                    if (status == 4 || status == 5) break; // o aparelho entendeu o pedido: não precisa tentar outro id
                }
            }
            return null;
        }
    }

    // ------------------------------------------------------------------ Controles GameSir (ex.: Cyclone 2) pelo receptor 2.4 GHz
    // Protocolo do GameSir Connect, documentado por projetos abertos (gamesir-reverse-engineering, cyclone2-linux):
    // na coleção FFF0/0040, o "heartbeat" 0F F2 faz o controle enviar relatórios 0x12 por ~1 s;
    // neles o byte 36 é a bateria (0-100) e o byte 35 vale 1 quando está no cabo/carregando.
    // O XInput não serve: ele informa esses controles como "com fio".
    class GameSirSource : IBatterySource
    {
        static readonly HashSet<ushort> CyclonePids = new HashSet<ushort> { 0x0575, 0x100B, 0x1053, 0x101D }; // receptor, XInput, cabo
        readonly Dictionary<string, Reading> last = new Dictionary<string, Reading>();

        public List<Reading> Poll()
        {
            var seen = new HashSet<string>();
            foreach (var info in Hid.Enumerate().Where(i => i.Vid == 0x3537 && i.UsagePage == 0xFFF0 && i.Usage == 0x0040))
            {
                // o PID e o nome mudam com o modo (0x0575 "GameSir-Cyclone 2" com o controle desligado,
                // 0x100B "Xbox 360 Controller for Windows" no XInput): a chave é a da marca
                string key = "gamesir:3537";
                string name = CyclonePids.Contains(info.Pid) ? "GameSir Cyclone 2"
                            : info.Product != null && info.Product.IndexOf("gamesir", StringComparison.OrdinalIgnoreCase) >= 0 ? info.Product.Replace('-', ' ').Trim()
                            : "Controle GameSir";
                if (seen.Contains(key)) continue; // já lido nesta rodada (por outra coleção)
                // quadros 0x12 todos zerados chegam quando o receptor não está ligado a um controle no modo XInput
                // (os analógicos parados valem 0x80): não trazem bateria
                var frame = Hid.Exchange(info, new byte[] { 0x0F, 0xF2, 0x00 }, f => f.Length > 36 && f[0] == 0x12, 1500);
                if (frame != null && frame.Skip(1).Take(35).All(b => b == 0)) frame = null;
                Diagnostics.Raw("GameSir 3537:" + info.Pid.ToString("X4") + " (FFF0/0040, heartbeat 0F F2; bytes 0-39)", Hid.LastFrame, 40);
                Reading r;
                if (!last.TryGetValue(key, out r)) r = new Reading { Key = key, Kind = DeviceKind.Controller };
                r.Name = name;
                if (frame != null && frame[36] <= 100)
                {
                    r.Percent = frame[36]; r.Charging = frame[35] == 1 && frame[36] < 100;
                    r.Online = true; r.Updated = DateTime.Now;
                    last[key] = r; seen.Add(key);
                }
            }
            foreach (var r in last.Values) if (!seen.Contains(r.Key)) r.Online = false; // controle desligado ou fora do XInput
            return last.Values.ToList();
        }
    }

    // ------------------------------------------------------------------ Mouses com receptor Beken/CompX "2.4G 8K HS Receiver" (VID 1D57)
    // Receptor genérico de vários mouses (AULA, Attack Shark, Delux, INPHIC...). Protocolo documentado por projetos
    // abertos (AreYouLowBattery, BatteryMonitor): na coleção 000A/0000 (5 bytes), o mouse envia sozinho, a cada ~2 s,
    // 03 <modelo> 40 <estado> <%>  ·  estado: 1 na bateria, 2 carregado (100%), 3 carregando (a % fica congelada).
    class BekenMouseSource : IBatterySource
    {
        // modelos confirmados por usuários do BatteryTray (nome completo, com a marca)
        static readonly Dictionary<byte, string> Confirmed = new Dictionary<byte, string> { { 0x50, "Delux M900 Pro" } };
        // código do modelo (byte 1) -> nome, da lista do projeto BatteryMonitor (devices/beken-1d57.json)
        static readonly Dictionary<byte, string> Models = new Dictionary<byte, string> {
            { 0x93, "IN10" }, { 0x42, "IN9" }, { 0x41, "IN6" }, { 0x92, "IN6 SE" }, { 0x90, "IN9 Ultra" }, { 0x99, "INX" },
            { 0x55, "X11" }, { 0x07, "X11 SE" }, { 0xBE, "X11 Pro" }, { 0xA9, "X11 Pro V2" }, { 0x4D, "X3" }, { 0x4F, "X3 Pro" }, { 0x4E, "X3 Max" },
            { 0x85, "X6" }, { 0x10, "R1" }, { 0xB1, "X1" }, { 0x06, "X8 Plus" }, { 0x08, "X8 Pro" }, { 0x02, "X8 SE" },
            { 0x04, "G3" }, { 0x0E, "G3 Pro" }, { 0x05, "F3" }, { 0x0D, "F7" }, { 0x0C, "F7 Pro" }, { 0x49, "F1" },
            { 0x24, "V6" }, { 0xE5, "V3 Pro" }, { 0xEA, "V3" }, { 0x65, "V2 MC" }, { 0x62, "V2 Ultra" }, { 0xCC, "V2 Ultra 3955" }, { 0x9E, "V2 MAX" }, { 0xCB, "V2" },
            { 0x2C, "M2" }, { 0xBC, "M3" }, { 0x14, "MV6" }, { 0x87, "M740" }, { 0x30, "M90 Pro" }, { 0x3D, "M900 Mini" }, { 0x2B, "M800 Mini" },
            { 0x43, "G15" }, { 0x77, "G11" }, { 0xC9, "G9 Pro" }, { 0x6C, "G25" }, { 0x61, "G64" }, { 0x9A, "MC031" }, { 0x6D, "DVSE4" }, { 0xC3, "Vanguard" } };
        static readonly HashSet<byte> TenSteps = new HashSet<byte> { 0x85, 0x10 }; // informam 0-10
        readonly Dictionary<string, Reading> last = new Dictionary<string, Reading>();

        public List<Reading> Poll()
        {
            var seen = new HashSet<string>();
            foreach (var info in Hid.Enumerate().Where(i => i.Vid == 0x1D57 && i.UsagePage == 0x000A && i.Usage == 0 && i.InLen == 5))
            {
                string key = "beken:" + info.Pid.ToString("X4");
                if (seen.Contains(key)) continue;
                seen.Add(key);
                var f = Hid.Exchange(info, null, x => x.Length >= 5 && x[0] == 0x03 && (x[2] == 0x40 || x[2] == 0x41), 2500);
                Reading r;
                if (!last.TryGetValue(key, out r)) r = new Reading { Key = key, Kind = DeviceKind.Mouse, Name = "Mouse sem fio (2.4G)" };
                Diagnostics.Raw("Receptor 1D57:" + info.Pid.ToString("X4") + " (000A/0000)", f, 5);
                if (f == null) { r.Online = false; continue; } // nenhum quadro em 2,5 s: mouse dormindo ou desligado
                string model;
                if (Confirmed.TryGetValue(f[1], out model)) r.Name = model;
                else if (Models.TryGetValue(f[1], out model)) r.Name = "Mouse " + model;
                int pct = TenSteps.Contains(f[1]) ? f[4] * 10 : f[4];
                if (f[3] == 2) pct = 100;
                if (pct >= 0 && pct <= 100 && !(f[3] == 3 && r.Percent >= 0)) r.Percent = pct; // carregando: mantém o último valor
                r.Charging = f[3] == 3;
                r.Online = true; r.Updated = DateTime.Now;
                r.LevelText = null;
                last[key] = r;
            }
            foreach (var r in last.Values) if (!seen.Contains(r.Key)) r.Online = false;
            return last.Values.ToList();
        }
    }

    // ------------------------------------------------------------------ Headsets MCHOSE (ex.: V9 Turbo+) pelo receptor 2.4 GHz
    // Protocolo documentado por projetos abertos (HaloBattery, myfinder-device-battery), testado no V9 Turbo+:
    // na coleção FF90/0001, o pedido 55 65 01 é respondido com 55 65 <%> <estado>  ·  estado 0x1A = headset desligado.
    // Carregando no cabo o headset para de responder, e na base responde como se estivesse na bateria.
    class MchoseHeadsetSource : IBatterySource
    {
        readonly Dictionary<string, Reading> last = new Dictionary<string, Reading>();

        public List<Reading> Poll()
        {
            var seen = new HashSet<string>();
            foreach (var info in Hid.Enumerate().Where(i => i.Vid == 0x3837 && i.UsagePage == 0xFF90 && i.Usage == 0x0001))
            {
                string key = "mchose:" + info.Pid.ToString("X4");
                if (seen.Contains(key)) continue;
                // às vezes o headset ignora o primeiro pedido: pergunta de novo (como o HaloBattery), até 3 vezes
                byte[] f = null; int tries = 0;
                while (f == null && tries++ < 3)
                    f = Hid.Exchange(info, new byte[] { 0x55, 0x65, 0x01 }, x => x.Length >= 4 && x[0] == 0x55 && x[1] == 0x65, 700);
                Diagnostics.Raw("MCHOSE 3837:" + info.Pid.ToString("X4") + " (FF90/0001, pedido 55 65 01, " + Math.Min(tries, 3) + " tentativa(s))", f, 8);
                Reading r;
                if (!last.TryGetValue(key, out r))
                    r = new Reading { Key = key, Kind = DeviceKind.Headset, Name = string.IsNullOrEmpty(info.Product) ? "MCHOSE" : info.Product.Trim() };
                bool ok = f != null && f[2] <= 100 && f[3] != 0x1A;
                if (ok)
                {
                    r.Percent = f[2]; r.Charging = f[3] == 0x03; r.Online = true; r.Updated = DateTime.Now;
                    last[key] = r; seen.Add(key);
                }
                else if (last.ContainsKey(key)) { r.Online = false; seen.Add(key); }
            }
            foreach (var r in last.Values) if (!seen.Contains(r.Key)) r.Online = false;
            return last.Values.ToList();
        }
    }

    // ------------------------------------------------------------------ Qualquer marca: campo de bateria padrão HID
    class GenericHidSource : IBatterySource
    {
        static readonly HashSet<ushort> HandledVendors = new HashSet<ushort> { 0x046D, 0x1532, 0x3537, 0x1D57, 0x3837 }; // têm fontes próprias
        readonly Dictionary<string, Reading> last = new Dictionary<string, Reading>();
        readonly Dictionary<string, DateTime> skipUntil = new Dictionary<string, DateTime>();

        public List<Reading> Poll()
        {
            var all = Hid.Enumerate().Where(i => !HandledVendors.Contains(i.Vid)).ToList();
            var seen = new HashSet<string>();
            foreach (var grp in all.GroupBy(i => i.Vid.ToString("X4") + ":" + i.Pid.ToString("X4")))
            {
                string key = "hid:" + grp.Key;
                DateTime until;
                if (skipUntil.TryGetValue(key, out until) && DateTime.UtcNow < until) continue;
                int pct = -1;
                foreach (var info in grp) { pct = Hid.ReadStandardBattery(info); if (pct >= 0) break; }
                if (pct < 0)
                {
                    if (!last.ContainsKey(key)) skipUntil[key] = DateTime.UtcNow.AddMinutes(10); // não informa bateria
                    continue;
                }
                Reading r;
                if (!last.TryGetValue(key, out r))
                {
                    string name = grp.Select(i => i.Product).FirstOrDefault(p => !string.IsNullOrEmpty(p)) ?? ("Aparelho " + grp.Key);
                    last[key] = r = new Reading { Key = key, Name = name, Kind = DeviceClassifier.Best(DeviceClassifier.FromHid(grp), DeviceClassifier.FromName(name)) };
                }
                r.Percent = pct; r.Online = true; r.Updated = DateTime.Now;
                seen.Add(key);
            }
            foreach (var r in last.Values) if (!seen.Contains(r.Key)) r.Online = false;
            return last.Values.ToList();
        }
    }

    // ------------------------------------------------------------------ Qualquer marca: bateria que o próprio Windows conhece (Bluetooth)
    class WindowsBatterySource : IBatterySource
    {
        [StructLayout(LayoutKind.Sequential)] struct SP_DEVINFO_DATA { public int cbSize; public Guid ClassGuid; public int DevInst; public IntPtr Reserved; }
        [StructLayout(LayoutKind.Sequential)] struct DEVPROPKEY { public Guid fmtid; public int pid; public DEVPROPKEY(string g, int p) { fmtid = new Guid(g); pid = p; } }
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] static extern IntPtr SetupDiGetClassDevs(IntPtr classGuid, string enumerator, IntPtr hwnd, int flags);
        [DllImport("setupapi.dll")] static extern bool SetupDiEnumDeviceInfo(IntPtr set, int index, ref SP_DEVINFO_DATA data);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] static extern bool SetupDiGetDeviceProperty(IntPtr set, ref SP_DEVINFO_DATA data, ref DEVPROPKEY key, out int type, byte[] buf, int size, out int req, int flags);
        [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

        static readonly DEVPROPKEY KeyBattery = new DEVPROPKEY("104EA319-6EE2-4701-BD47-8DDBF425BBE5", 2);      // % de bateria
        static readonly DEVPROPKEY KeyFriendly = new DEVPROPKEY("a45c254e-df1c-4efd-8020-67d146a850e0", 14);
        static readonly DEVPROPKEY KeyDesc = new DEVPROPKEY("a45c254e-df1c-4efd-8020-67d146a850e0", 2);
        static readonly DEVPROPKEY KeyContainer = new DEVPROPKEY("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c", 2);
        static readonly DEVPROPKEY KeyCod = new DEVPROPKEY("2bd67d8b-8beb-48d5-87e0-6cda3428040a", 10);        // Bluetooth Class of Device

        readonly Dictionary<string, Reading> last = new Dictionary<string, Reading>();

        static byte[] Prop(IntPtr set, ref SP_DEVINFO_DATA d, DEVPROPKEY key, out int type)
        {
            int req; var buf = new byte[512];
            if (!SetupDiGetDeviceProperty(set, ref d, ref key, out type, buf, buf.Length, out req, 0)) return null;
            var r = new byte[Math.Min(req, buf.Length)]; Array.Copy(buf, r, r.Length); return r;
        }

        static string Str(byte[] b) { return b == null ? null : Encoding.Unicode.GetString(b).TrimEnd('\0'); }

        public List<Reading> Poll()
        {
            var seen = new HashSet<string>();
            IntPtr set = SetupDiGetClassDevs(IntPtr.Zero, null, IntPtr.Zero, 0x2 | 0x4); // PRESENT | ALLCLASSES
            try
            {
                var d = new SP_DEVINFO_DATA(); d.cbSize = Marshal.SizeOf(d);
                for (int i = 0; SetupDiEnumDeviceInfo(set, i, ref d); i++)
                {
                    int t;
                    var b = Prop(set, ref d, KeyBattery, out t);
                    if (b == null || b.Length < 1 || b[0] > 100) continue;
                    var cont = Prop(set, ref d, KeyContainer, out t);
                    string key = "win:" + (cont != null && cont.Length == 16 ? new Guid(cont).ToString() : i.ToString());
                    if (seen.Contains(key)) continue;
                    seen.Add(key);
                    string name = Str(Prop(set, ref d, KeyFriendly, out t)) ?? Str(Prop(set, ref d, KeyDesc, out t)) ?? "Bluetooth";
                    var cod = Prop(set, ref d, KeyCod, out t);
                    var kind = DeviceClassifier.Best(cod != null && cod.Length >= 4 ? DeviceClassifier.FromCod(BitConverter.ToUInt32(cod, 0)) : DeviceKind.Other,
                                                     DeviceClassifier.FromName(name));
                    Reading r;
                    if (!last.TryGetValue(key, out r)) last[key] = r = new Reading { Key = key };
                    r.Name = name; r.Kind = kind; r.Percent = b[0]; r.Updated = DateTime.Now;
                    r.Online = true; // o Windows mantém o último valor conhecido do aparelho pareado
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
            foreach (var r in last.Values) if (!seen.Contains(r.Key)) r.Online = false;
            return last.Values.ToList();
        }

        static readonly DEVPROPKEY KeyEnumerator = new DEVPROPKEY("a45c254e-df1c-4efd-8020-67d146a850e0", 24);
        static readonly Guid BluetoothClass = new Guid("e0cbf06c-cd8b-4647-bb8a-263b43f0f974");

        public class DiagDevice { public string Name, Bus; public DeviceKind Kind; public int Battery = -1; }

        // Diagnóstico: aparelhos Bluetooth pareados e tudo que informa bateria ao Windows,
        // agrupados pelo "container" (as várias partes de um mesmo aparelho físico).
        public static List<DiagDevice> Diagnose()
        {
            var res = new Dictionary<string, DiagDevice>();
            IntPtr set = SetupDiGetClassDevs(IntPtr.Zero, null, IntPtr.Zero, 0x2 | 0x4);
            try
            {
                var d = new SP_DEVINFO_DATA(); d.cbSize = Marshal.SizeOf(d);
                for (int i = 0; SetupDiEnumDeviceInfo(set, i, ref d); i++)
                {
                    int t;
                    string en = (Str(Prop(set, ref d, KeyEnumerator, out t)) ?? "").ToUpperInvariant();
                    bool bt = d.ClassGuid == BluetoothClass && (en == "BTHENUM" || en == "BTHLE");
                    var b = Prop(set, ref d, KeyBattery, out t);
                    bool hasBattery = b != null && b.Length >= 1 && b[0] <= 100;
                    if (!bt && !hasBattery) continue;
                    var cont = Prop(set, ref d, KeyContainer, out t);
                    string key = cont != null && cont.Length == 16 ? new Guid(cont).ToString() : "#" + i;
                    DiagDevice dd;
                    if (!res.TryGetValue(key, out dd)) res[key] = dd = new DiagDevice();
                    string name = Str(Prop(set, ref d, KeyFriendly, out t)) ?? Str(Prop(set, ref d, KeyDesc, out t));
                    if (bt || dd.Name == null)
                    {
                        dd.Name = name ?? dd.Name;
                        dd.Bus = en == "BTHLE" ? "Bluetooth LE" : en == "BTHENUM" ? "Bluetooth" : en;
                    }
                    var cod = Prop(set, ref d, KeyCod, out t);
                    if (cod != null && cod.Length >= 4) dd.Kind = DeviceClassifier.Best(DeviceClassifier.FromCod(BitConverter.ToUInt32(cod, 0)), dd.Kind);
                    if (hasBattery) dd.Battery = b[0];
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
            foreach (var dd in res.Values) dd.Kind = DeviceClassifier.Best(dd.Kind, DeviceClassifier.FromName(dd.Name));
            return res.Values.OrderBy(x => x.Name).ToList();
        }
    }

    // ------------------------------------------------------------------ XInput
    class XInputSource : IBatterySource
    {
        [StructLayout(LayoutKind.Sequential)] struct BAT { public byte Type; public byte Level; }
        [DllImport("xinput1_4.dll")] static extern int XInputGetBatteryInformation(int user, byte devType, out BAT b);

        public List<Reading> Poll()
        {
            var res = new List<Reading>();
            for (int i = 0; i < 4; i++)
            {
                BAT b;
                int r;
                try { r = XInputGetBatteryInformation(i, 0, out b); } catch { return res; }
                // 0=desconectado, 1=com fio (o dongle não informa bateria) -> ignora
                if (r != 0 || b.Type == 0 || b.Type == 1) continue;
                var pct = new[] { 5, 30, 65, 100 }[Math.Min((int)b.Level, 3)];
                res.Add(new Reading
                {
                    Key = "xinput:" + i,
                    Name = "Controle " + (i + 1),
                    Kind = DeviceKind.Controller,
                    Percent = pct,
                    LevelText = new[] { "vazia", "baixa", "média", "cheia" }[Math.Min((int)b.Level, 3)],
                    Online = true,
                    Updated = DateTime.Now
                });
            }
            return res;
        }

        // Diagnóstico: o que cada uma das 4 portas XInput informa.
        public static List<string> Diagnose()
        {
            var res = new List<string>();
            string[] types = { "desconectado", "com fio (o Windows não recebe a bateria; acontece com alguns receptores sem fio)", "pilhas alcalinas", "bateria NiMH" };
            string[] levels = { "vazia", "baixa", "média", "cheia" };
            for (int i = 0; i < 4; i++)
            {
                BAT b; int r;
                try { r = XInputGetBatteryInformation(i, 0, out b); }
                catch (Exception ex) { res.Add("XInput indisponível: " + ex.Message); break; }
                string line = "Porta " + (i + 1) + ": ";
                if (r == 1167) line += "vazia";
                else if (r != 0) line += "erro " + r;
                else if (b.Type == 0) line += "vazia";
                else
                {
                    line += "controle conectado, " + (b.Type < types.Length ? types[b.Type] : b.Type == 0xFF ? "tipo de bateria desconhecido" : "tipo " + b.Type);
                    if (b.Type >= 2) line += ", nível " + levels[Math.Min((int)b.Level, 3)];
                }
                res.Add(line);
            }
            return res;
        }
    }

    // ------------------------------------------------------------------ Desenho de um aparelho: ícone em cima, valor embaixo
    static class SlotRenderer
    {
        const string IconFont = "Segoe Fluent Icons";
        const string TextFont = "Segoe UI Variable Text";
        public const string Bolt = "";

        public static string Glyph(DeviceKind k)
        {
            switch (k)
            {
                case DeviceKind.Mouse: return "";
                case DeviceKind.Headset: return "";
                case DeviceKind.Keyboard: return "";
                case DeviceKind.Controller: return "";
                default: return ""; // bateria
            }
        }

        public static bool LightTheme()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
            {
                object v = k == null ? null : k.GetValue("SystemUsesLightTheme");
                return v is int && (int)v == 1;
            }
        }

        static Font MakeFont(string family, float px, string fallback)
        {
            var f = new Font(family, px, FontStyle.Regular, GraphicsUnit.Pixel);
            if (f.Name != family) { f.Dispose(); f = new Font(fallback, px, FontStyle.Regular, GraphicsUnit.Pixel); }
            return f;
        }

        static Color Foreground(Reading r)
        {
            Color fg = LightTheme() ? Color.FromArgb(20, 20, 20) : Color.White;
            if (!r.Online) return Color.FromArgb(110, fg);
            if (r.Percent >= 0 && r.Percent <= 15 && !r.Charging) return Color.FromArgb(255, 90, 90);
            return fg;
        }

        public static string Number(Reading r) { return r.Percent < 0 ? "?" : r.Percent.ToString(); }
        public static bool ShowBolt(Reading r) { return r.Charging && r.Online; }

        // Bitmap do tamanho do espaço do ícone na barra (ex.: 32 x 48).
        public static Bitmap Render(Reading r, int w, int h, float scale)
        {
            var bmp = new Bitmap(Math.Max(1, w), Math.Max(1, h), PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            using (var br = new SolidBrush(Foreground(r)))
            using (var fmt = (StringFormat)StringFormat.GenericTypographic.Clone())
            {
                g.Clear(Color.FromArgb(1, 0, 0, 0)); // quase transparente: deixa o clique funcionar
                g.TextRenderingHint = TextRenderingHint.AntiAlias;
                fmt.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
                float cy = h / 2f;

                using (var gf = MakeFont(IconFont, 16 * scale, "Segoe MDL2 Assets"))
                {
                    string glyph = Glyph(r.Kind);
                    float gw = g.MeasureString(glyph, gf, 1000, fmt).Width;
                    g.DrawString(glyph, gf, br, (w - gw) / 2f, cy - 15 * scale, fmt);
                }

                // valor + "%" (ou raio, carregando); encolhe a fonte se não couber
                string num = Number(r);
                bool bolt = ShowBolt(r);
                for (float px = 10.5f * scale; px >= 7 * scale; px -= 0.5f * scale)
                {
                    string family = num.Length >= 3 ? "Bahnschrift SemiCondensed" : TextFont;
                    using (var tf = MakeFont(family, px, "Segoe UI"))
                    using (var bf = MakeFont(IconFont, px * 0.85f, "Segoe MDL2 Assets"))
                    {
                        string text = bolt ? num : num + "%";
                        float tw = g.MeasureString(text, tf, 1000, fmt).Width;
                        float bw = bolt ? g.MeasureString(Bolt, bf, 1000, fmt).Width + 0.5f : 0;
                        if (tw + bw > w - 1 && px > 7 * scale + 0.01f) continue;
                        float x = (w - tw - bw) / 2f, y = cy + 4 * scale;
                        g.DrawString(text, tf, br, x, y, fmt);
                        if (bolt) g.DrawString(Bolt, bf, br, x + tw + 0.5f, y + px * 0.15f, fmt);
                        break;
                    }
                }
            }
            return bmp;
        }

        // Ícone do próprio app (bateria), usado quando não há nenhum aparelho visível.
        public static IntPtr AppIcon()
        {
            using (var bmp = new Bitmap(16, 16, PixelFormat.Format32bppArgb))
            using (var g = Graphics.FromImage(bmp))
            using (var f = MakeFont(IconFont, 15, "Segoe MDL2 Assets"))
            using (var fmt = (StringFormat)StringFormat.GenericTypographic.Clone())
            {
                g.TextRenderingHint = TextRenderingHint.AntiAlias;
                g.Clear(Color.Transparent);
                var sz = g.MeasureString("", f, 100, fmt);
                g.DrawString("", f, Brushes.White, (16 - sz.Width) / 2f, (16 - sz.Height) / 2f, fmt);
                return bmp.GetHicon();
            }
        }

        // Ícone de 16 px usado enquanto o aparelho ainda não foi fixado (aparece dentro do "^").
        public static IntPtr SmallIcon(Reading r)
        {
            int s = 16;
            using (var bmp = new Bitmap(s, s, PixelFormat.Format32bppArgb))
            using (var g = Graphics.FromImage(bmp))
            using (var fmt = (StringFormat)StringFormat.GenericTypographic.Clone())
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                string text = Number(r);
                using (var ff = new FontFamily(text.Length >= 3 ? "Bahnschrift SemiBold Condensed" : "Bahnschrift SemiBold"))
                {
                    for (float em = 15; em >= 5; em -= 0.5f)
                    {
                        using (var p = new GraphicsPath())
                        {
                            p.AddString(text, ff, (int)FontStyle.Regular, em, PointF.Empty, fmt);
                            var b = p.GetBounds();
                            if (b.Width > s - 1 || b.Height > s - 2) continue;
                            using (var m = new Matrix()) { m.Translate((s - b.Width) / 2f - b.X, (s - b.Height) / 2f - b.Y); p.Transform(m); }
                            g.FillPath(Brushes.White, p);
                            break;
                        }
                    }
                }
                return bmp.GetHicon();
            }
        }

        public static string Describe(Reading r)
        {
            string pct = r.Percent < 0 ? "?" : (r.LevelText != null ? "~" + r.Percent + "% (" + r.LevelText + ")" : r.Percent + "%");
            string status = !r.Online ? " — desligado, " + (r.Updated > DateTime.MinValue ? "lido " + Ago(r.Updated) : "último valor") : r.Charging ? " — carregando" : "";
            return r.Name + ": " + pct + status;
        }

        // "há 5 min", "há 3 h", "ontem às 22:00", "em 12/11"
        public static string Ago(DateTime t)
        {
            var d = DateTime.Now - t;
            if (d.TotalMinutes < 2) return "agora há pouco";
            if (d.TotalMinutes < 60) return "há " + (int)d.TotalMinutes + " min";
            if (t.Date == DateTime.Today) return "há " + (int)d.TotalHours + " h";
            if (t.Date == DateTime.Today.AddDays(-1)) return "ontem às " + t.ToString("HH:mm");
            return "em " + t.ToString("dd/MM");
        }
    }

    // ------------------------------------------------------------------ Funções nativas compartilhadas
    static class Native
    {
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
        [StructLayout(LayoutKind.Sequential)] public struct SIZE { public int cx, cy; }
        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int x, y; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] public struct BLENDFUNCTION { public byte Op, Flags, Alpha, Format; }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string c, string n);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern bool UpdateLayeredWindow(IntPtr h, IntPtr dstDc, IntPtr pptDst, ref SIZE size, IntPtr srcDc, ref POINT pptSrc, int key, ref BLENDFUNCTION blend, int flags);
        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [DllImport("user32.dll")] public static extern int GetDpiForWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr h);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr o);

        public static IntPtr Taskbar() { return FindWindow("Shell_TrayWnd", null); }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string c, string n);
        [StructLayout(LayoutKind.Sequential)] public struct TRACKMOUSEEVENT { public int cbSize, dwFlags; public IntPtr hwndTrack; public int dwHoverTime; }
        [DllImport("user32.dll")] public static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT t);

        // Retângulo (tela) de uma janela interna da barra: "TrayNotifyWnd" (bandeja + relógio) ou "ReBarWindow32" (apps abertos).
        public static Rectangle TaskbarPart(string cls)
        {
            IntPtr tb = Taskbar(), h = tb == IntPtr.Zero ? IntPtr.Zero : FindWindowEx(tb, IntPtr.Zero, cls, null);
            RECT r;
            if (h == IntPtr.Zero || !GetWindowRect(h, out r)) return Rectangle.Empty;
            return Rectangle.FromLTRB(r.L, r.T, r.R, r.B);
        }

        public static Rectangle TaskbarRect()
        {
            RECT r;
            IntPtr tb = Taskbar();
            if (tb == IntPtr.Zero || !GetWindowRect(tb, out r)) return Rectangle.Empty;
            return Rectangle.FromLTRB(r.L, r.T, r.R, r.B);
        }

        public static float Scale()
        {
            try { int d = GetDpiForWindow(Taskbar()); return d > 0 ? d / 96f : 1f; } catch { return 1f; }
        }
    }

    // ------------------------------------------------------------------ Texto de um aparelho, desenhado dentro da barra sobre o ícone dele
    class SlotOverlay : NativeWindow, IDisposable
    {
        IntPtr taskbar;
        string lastSig;
        public Action LeftClick;

        void EnsureAttached()
        {
            IntPtr tb = Native.Taskbar();
            if (tb == IntPtr.Zero) return;
            if (tb == taskbar && Handle != IntPtr.Zero && Native.IsWindow(Handle)) return;
            if (Handle != IntPtr.Zero) { try { DestroyHandle(); } catch { } }
            taskbar = tb; lastSig = null;
            CreateHandle(new CreateParams
            {
                Caption = "BatteryTray",
                Parent = tb,
                Style = unchecked((int)(0x40000000 | 0x04000000)), // WS_CHILD | WS_CLIPSIBLINGS (fica oculta até ter posição)
                // WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT:
                // o mouse "atravessa" o texto e chega no ícone real da bandeja (dica com nome e %, cliques e arrastar).
                ExStyle = 0x00080000 | 0x08000000 | 0x80 | 0x20,
                Width = 1, Height = 1
            });
        }

        // screen: retângulo (tela) do ícone fixado do aparelho; null = esconder.
        public void Show(Reading r, Rectangle? screen)
        {
            EnsureAttached();
            if (Handle == IntPtr.Zero) return;
            if (!screen.HasValue)
            {
                if (lastSig != "hidden") { Native.ShowWindow(Handle, 0); lastSig = "hidden"; }
                return;
            }
            Native.RECT wr, cr;
            Native.GetWindowRect(taskbar, out wr);
            Native.GetClientRect(taskbar, out cr);
            int h = cr.B - cr.T, w = screen.Value.Width, x = screen.Value.Left - wr.L;
            float scale = Native.Scale();
            string sig = string.Join("|", x, w, h, scale, r.Percent, r.Online, r.Charging, r.Kind, SlotRenderer.LightTheme());
            if (sig == lastSig) return;
            lastSig = sig;
            using (var bmp = SlotRenderer.Render(r, w, h, scale))
            {
                Native.SetWindowPos(Handle, IntPtr.Zero /*HWND_TOP*/, x, 0, w, h, 0x0010 | 0x0040); // NOACTIVATE | SHOWWINDOW
                Blit(bmp);
            }
        }

        void Blit(Bitmap bmp)
        {
            IntPtr screen = Native.GetDC(IntPtr.Zero), mem = Native.CreateCompatibleDC(screen);
            IntPtr hbmp = bmp.GetHbitmap(Color.FromArgb(0)), old = Native.SelectObject(mem, hbmp);
            var size = new Native.SIZE { cx = bmp.Width, cy = bmp.Height }; var src = new Native.POINT();
            var blend = new Native.BLENDFUNCTION { Op = 0, Flags = 0, Alpha = 255, Format = 1 /*AC_SRC_ALPHA*/ };
            Native.UpdateLayeredWindow(Handle, screen, IntPtr.Zero, ref size, mem, ref src, 0, ref blend, 2 /*ULW_ALPHA*/);
            Native.SelectObject(mem, old); Native.DeleteObject(hbmp); Native.DeleteDC(mem); Native.ReleaseDC(IntPtr.Zero, screen);
        }

        public void Invalidate() { lastSig = null; }

        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case 0x21: m.Result = (IntPtr)3; return;                                   // WM_MOUSEACTIVATE -> MA_NOACTIVATE
                case 0x202: if (LeftClick != null) LeftClick(); return;                    // WM_LBUTTONUP
            }
            base.WndProc(ref m);
        }

        public void Dispose() { if (Handle != IntPtr.Zero) DestroyHandle(); }
    }

    // ------------------------------------------------------------------ Aparência: modo, opções do painel e cor/nome curto/ordem de cada aparelho
    class Appearance
    {
        const string KeyPath = @"Software\BatteryTray\Aparencia";
        public bool Panel = true;                 // painel na barra (padrão) ou um ícone de bandeja por aparelho
        public bool ShowBar = true, ShowPercent = true, ShowLabels = true;
        public int Size = 1;                      // 0 compacto, 1 normal, 2 grande
        public readonly Dictionary<string, int> Colors = new Dictionary<string, int>();     // ARGB escolhido pelo usuário
        public readonly Dictionary<string, string> Labels = new Dictionary<string, string>(); // nome curto (ex.: "600")
        public readonly List<string> Order = new List<string>();

        public static Color DefaultColor(DeviceKind k)
        {
            switch (k)
            {
                case DeviceKind.Headset: return Color.FromArgb(255, 92, 40);
                case DeviceKind.Mouse: return Color.FromArgb(40, 170, 255);
                case DeviceKind.Keyboard: return Color.FromArgb(60, 200, 120);
                case DeviceKind.Controller: return Color.FromArgb(80, 120, 255);
                default: return Color.FromArgb(170, 170, 170);
            }
        }

        public Color ColorOf(Reading r) { int c; return Colors.TryGetValue(r.Key, out c) ? Color.FromArgb(c) : DefaultColor(r.Kind); }
        public string LabelOf(string key) { string s; return Labels.TryGetValue(key, out s) ? s : ""; }
        public int OrderOf(string key) { int i = Order.IndexOf(key); return i < 0 ? int.MaxValue : i; }

        public static Appearance Load()
        {
            var a = new Appearance();
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(KeyPath))
                {
                    if (k == null) return a;
                    a.Panel = (k.GetValue("Modo") as string ?? "painel") != "icones";
                    a.ShowBar = Flag(k, "Barra", true); a.ShowPercent = Flag(k, "Porcentagem", true); a.ShowLabels = Flag(k, "NomesCurtos", true);
                    object sz = k.GetValue("Tamanho"); if (sz is int) a.Size = Math.Max(0, Math.Min(2, (int)sz));
                    var ord = k.GetValue("Ordem") as string[]; if (ord != null) a.Order.AddRange(ord);
                    foreach (var n in k.GetValueNames())
                    {
                        if (n.StartsWith("Cor:") && k.GetValue(n) is int) a.Colors[n.Substring(4)] = (int)k.GetValue(n);
                        else if (n.StartsWith("Nome:")) a.Labels[n.Substring(5)] = k.GetValue(n) as string ?? "";
                    }
                }
            }
            catch { }
            return a;
        }

        static bool Flag(RegistryKey k, string name, bool def) { object v = k.GetValue(name); return v is int ? (int)v != 0 : def; }

        public void Save()
        {
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(KeyPath, false);
                using (var k = Registry.CurrentUser.CreateSubKey(KeyPath))
                {
                    k.SetValue("Modo", Panel ? "painel" : "icones");
                    k.SetValue("Barra", ShowBar ? 1 : 0, RegistryValueKind.DWord);
                    k.SetValue("Porcentagem", ShowPercent ? 1 : 0, RegistryValueKind.DWord);
                    k.SetValue("NomesCurtos", ShowLabels ? 1 : 0, RegistryValueKind.DWord);
                    k.SetValue("Tamanho", Size, RegistryValueKind.DWord);
                    k.SetValue("Ordem", Order.ToArray(), RegistryValueKind.MultiString);
                    foreach (var kv in Colors) k.SetValue("Cor:" + kv.Key, kv.Value, RegistryValueKind.DWord);
                    foreach (var kv in Labels) if (kv.Value != "") k.SetValue("Nome:" + kv.Key, kv.Value);
                }
            }
            catch { }
        }
    }

    // ------------------------------------------------------------------ Desenho do painel: ícone colorido, % ao lado e barra de bateria embaixo
    static class PanelRenderer
    {
        public class Item { public Reading R; public Color Color; public string Label; public int X, W; public float ColA; }

        // Cada aparelho é uma grade 2x2:   ícone | porcentagem
        //                                  nome  | barra
        // Sem nome, o ícone fica centralizado na altura; sem barra, a porcentagem também.
        static readonly float[] IconPx = { 16, 19, 23 }, TextPx = { 13, 15.5f, 18.5f }, LabelPx = { 8.5f, 9.5f, 11f };
        const float PadL = 6, Gap = 6, PadR = 8, StackGap = 3;
        const string IconFont = "Segoe Fluent Icons";

        static FontFamily Family(string name, string fallback)
        {
            try { return new FontFamily(name); } catch (ArgumentException) { return new FontFamily(fallback); }
        }

        // Contorno do texto: as medidas vêm do desenho real das letras, sem as sobras da fonte
        static GraphicsPath TextPath(string text, FontFamily fam, FontStyle st, float px)
        {
            var p = new GraphicsPath();
            using (var fmt = (StringFormat)StringFormat.GenericTypographic.Clone())
                p.AddString(text, fam, (int)st, px, PointF.Empty, fmt);
            return p;
        }

        static void FillAt(Graphics g, GraphicsPath p, Color c, float dx, float dy)
        {
            using (var m = new Matrix()) { m.Translate(dx, dy); p.Transform(m); }
            using (var b = new SolidBrush(c)) g.FillPath(b, p);
        }

        static bool HasLabel(Appearance a, Item it) { return a.ShowLabels && !string.IsNullOrEmpty(it.Label); }

        // Posiciona os itens (X, W) e devolve a largura total. A coluna da porcentagem tem sempre a largura de "100%",
        // para o painel não mudar de tamanho quando o valor muda; a do ícone cresce para caber o nome curto.
        public static int Layout(List<Item> items, Appearance a, float s)
        {
            using (var tfam = Family("Segoe UI Variable Display", "Segoe UI"))
            using (var lfam = Family("Segoe UI", "Arial"))
            using (var reference = TextPath(a.ShowPercent ? "100%" : "100", tfam, FontStyle.Bold, TextPx[a.Size] * s))
            {
                float colB = reference.GetBounds().Width;
                int x = 0;
                foreach (var it in items)
                {
                    float colA = IconPx[a.Size] * s;
                    if (HasLabel(a, it))
                        using (var lp = TextPath(it.Label, lfam, FontStyle.Bold, LabelPx[a.Size] * s))
                            colA = Math.Max(colA, lp.GetBounds().Width);
                    it.ColA = colA;
                    it.X = x;
                    it.W = (int)Math.Ceiling((PadL + Gap + PadR) * s + colA + colB);
                    x += it.W;
                }
                return x;
            }
        }

        public static Bitmap Render(List<Item> items, Appearance a, float s, int w, int h, bool light, int hover)
        {
            var bmp = new Bitmap(Math.Max(1, w), Math.Max(1, h), PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            using (var ifam = Family(IconFont, "Segoe MDL2 Assets"))
            using (var tfam = Family("Segoe UI Variable Display", "Segoe UI"))
            using (var lfam = Family("Segoe UI", "Arial"))
            using (var reference = TextPath(a.ShowPercent ? "100%" : "100", tfam, FontStyle.Bold, TextPx[a.Size] * s))
            {
                g.Clear(Color.FromArgb(1, 0, 0, 0)); // quase transparente: o painel recebe o mouse
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Color fg = light ? Color.FromArgb(26, 26, 26) : Color.White;
                Color low = light ? Color.FromArgb(196, 43, 28) : Color.FromArgb(255, 95, 95);

                // destaque ao passar o mouse: um só para o grupo todo, como o do relógio
                if (hover >= 0)
                    using (var b = new SolidBrush(Color.FromArgb(light ? 18 : 22, fg)))
                    using (var p = RoundRect(new RectangleF(1 * s, 4 * s, w - 2 * s, h - 8 * s), 5 * s))
                        g.FillPath(b, p);

                // linhas da grade: porcentagem em cima e barra embaixo (sem barra, a porcentagem fica no meio)
                var rb = reference.GetBounds();
                float colB = rb.Width, textH = rb.Height;
                float barH = a.ShowBar ? 3 * s : 0, barGap = a.ShowBar ? 5 * s : 0;
                float textTop = (h - (textH + barGap + barH)) / 2f, barY = textTop + textH + barGap;

                foreach (var it in items)
                {
                    var r = it.R;
                    bool isLow = r.Online && r.Percent >= 0 && r.Percent <= 15 && !r.Charging;
                    Color dev = !r.Online ? Color.FromArgb(120, 140, 140, 140) : isLow ? low : it.Color;
                    Color text = !r.Online ? Color.FromArgb(120, fg) : isLow ? low : fg;
                    float colAX = it.X + PadL * s, colBX = colAX + it.ColA + Gap * s;

                    // coluna da direita: porcentagem (ou raio, carregando) e barra
                    string num = SlotRenderer.Number(r);
                    bool bolt = SlotRenderer.ShowBolt(r);
                    using (var tp = TextPath(bolt || !a.ShowPercent ? num : num + "%", tfam, FontStyle.Bold, TextPx[a.Size] * s))
                    {
                        var tb = tp.GetBounds();
                        FillAt(g, tp, text, colBX - tb.X, textTop - rb.Y);
                        if (bolt)
                            using (var bp = TextPath(SlotRenderer.Bolt, ifam, FontStyle.Regular, TextPx[a.Size] * 0.85f * s))
                            {
                                var bb = bp.GetBounds();
                                FillAt(g, bp, dev, colBX + tb.Width + 2 * s - bb.X, textTop + (textH - bb.Height) / 2f - bb.Y);
                            }
                    }
                    if (a.ShowBar)
                    {
                        using (var track = new SolidBrush(Color.FromArgb(light ? 40 : 55, fg))) g.FillRectangle(track, colBX, barY, colB, barH);
                        float pct = r.Percent < 0 ? 0 : Math.Min(100, r.Percent) / 100f;
                        using (var fill = new SolidBrush(dev)) g.FillRectangle(fill, colBX, barY, colB * pct, barH);
                    }

                    // coluna da esquerda: ícone na cor do aparelho e, se houver, o nome curto embaixo
                    using (var ip = TextPath(SlotRenderer.Glyph(r.Kind), ifam, FontStyle.Regular, IconPx[a.Size] * s))
                    {
                        var ib = ip.GetBounds();
                        float iconTop = (h - ib.Height) / 2f;
                        if (HasLabel(a, it))
                            using (var lp = TextPath(it.Label, lfam, FontStyle.Bold, LabelPx[a.Size] * s))
                            {
                                var lb = lp.GetBounds();
                                float labelTop;
                                if (a.ShowBar)
                                {
                                    labelTop = barY + barH / 2f - lb.Height / 2f;       // nome na linha da barra
                                    iconTop = Math.Max(2 * s, labelTop - StackGap * s - ib.Height);
                                }
                                else
                                {
                                    iconTop = (h - (ib.Height + StackGap * s + lb.Height)) / 2f; // ícone + nome no meio
                                    labelTop = iconTop + ib.Height + StackGap * s;
                                }
                                Color lc = !r.Online ? text : Color.FromArgb(light ? 200 : 215, fg);
                                FillAt(g, lp, lc, colAX + (it.ColA - lb.Width) / 2f - lb.X, labelTop - lb.Y);
                            }
                        FillAt(g, ip, dev, colAX + (it.ColA - ib.Width) / 2f - ib.X, iconTop - ib.Y);
                    }
                }
            }
            return bmp;
        }

        public static GraphicsPath RoundRect(RectangleF r, float rad)
        {
            var p = new GraphicsPath(); float d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure(); return p;
        }
    }

    // ------------------------------------------------------------------ Painel dentro da barra de tarefas, à esquerda da bandeja
    // Janela filha da barra (como a do TrafficMonitor). Diferente do desenho sobre os ícones da bandeja,
    // o painel recebe o mouse: destaque, dica, clique (atualiza) e clique direito (menu) são do próprio app.
    class PanelWindow : NativeWindow, IDisposable
    {
        IntPtr taskbar;
        string lastSig;
        List<PanelRenderer.Item> items = new List<PanelRenderer.Item>();
        Appearance look;
        float scale = 1;
        int hover = -1;
        bool tracking;
        readonly TipWindow tip = new TipWindow();
        public Action LeftClick, RightClick;

        void EnsureAttached()
        {
            IntPtr tb = Native.Taskbar();
            if (tb == IntPtr.Zero) return;
            if (tb == taskbar && Handle != IntPtr.Zero && Native.IsWindow(Handle)) return;
            if (Handle != IntPtr.Zero) { try { DestroyHandle(); } catch { } }
            taskbar = tb; lastSig = null;
            CreateHandle(new CreateParams
            {
                Caption = "BatteryTrayPanel",
                Parent = tb,
                Style = unchecked((int)(0x40000000 | 0x04000000)),  // WS_CHILD | WS_CLIPSIBLINGS
                ExStyle = 0x00080000 | 0x08000000 | 0x80,          // WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
                Width = 1, Height = 1
            });
        }

        public bool Visible { get; private set; }

        // Mostra os itens encostados à esquerda da bandeja. Devolve false (e esconde) se não houver espaço
        // entre os apps abertos e a bandeja.
        public bool Show(List<PanelRenderer.Item> list, Appearance a)
        {
            EnsureAttached();
            if (Handle == IntPtr.Zero) return false;
            items = list; look = a; scale = Native.Scale();
            Rectangle tray = Native.TaskbarPart("TrayNotifyWnd"), apps = Native.TaskbarPart("ReBarWindow32");
            Native.RECT wr, cr;
            Native.GetWindowRect(taskbar, out wr);
            Native.GetClientRect(taskbar, out cr);
            int h = cr.B - cr.T;
            int w = PanelRenderer.Layout(items, a, scale);
            int x = tray.IsEmpty ? -1 : tray.Left - wr.L - (int)(4 * scale) - w;
            bool fits = items.Count > 0 && x >= 0 && (apps.IsEmpty || x >= apps.Right - wr.L + (int)(12 * scale));
            if (!fits) { Hide(); return false; }
            bool light = SlotRenderer.LightTheme();
            string sig = string.Join("|", x, w, h, scale, light, hover, a.ShowBar, a.ShowPercent, a.ShowLabels, a.Size,
                string.Join(";", items.Select(i => i.R.Key + i.R.Percent + i.R.Online + i.R.Charging + i.R.Kind + i.Color.ToArgb() + i.Label)));
            if (sig == lastSig && Visible) return true;
            lastSig = sig;
            using (var bmp = PanelRenderer.Render(items, a, scale, w, h, light, hover))
            {
                Native.SetWindowPos(Handle, IntPtr.Zero /*HWND_TOP*/, x, 0, w, h, 0x0010 | 0x0040); // NOACTIVATE | SHOWWINDOW
                Blit(bmp);
            }
            Visible = true;
            return true;
        }

        public void Hide()
        {
            if (Handle != IntPtr.Zero && Visible) Native.ShowWindow(Handle, 0);
            Visible = false; lastSig = null; tip.Hide();
        }

        public void Invalidate() { lastSig = null; }

        void Blit(Bitmap bmp)
        {
            IntPtr screen = Native.GetDC(IntPtr.Zero), mem = Native.CreateCompatibleDC(screen);
            IntPtr hbmp = bmp.GetHbitmap(Color.FromArgb(0)), old = Native.SelectObject(mem, hbmp);
            var size = new Native.SIZE { cx = bmp.Width, cy = bmp.Height }; var src = new Native.POINT();
            var blend = new Native.BLENDFUNCTION { Op = 0, Flags = 0, Alpha = 255, Format = 1 /*AC_SRC_ALPHA*/ };
            Native.UpdateLayeredWindow(Handle, screen, IntPtr.Zero, ref size, mem, ref src, 0, ref blend, 2 /*ULW_ALPHA*/);
            Native.SelectObject(mem, old); Native.DeleteObject(hbmp); Native.DeleteDC(mem); Native.ReleaseDC(IntPtr.Zero, screen);
        }

        int ItemAt(int x) { for (int i = 0; i < items.Count; i++) if (x >= items[i].X && x < items[i].X + items[i].W) return i; return -1; }

        void Redraw() { if (look != null && Visible) { lastSig = null; Show(items, look); } }

        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case 0x21: m.Result = (IntPtr)3; return;                         // WM_MOUSEACTIVATE -> MA_NOACTIVATE
                case 0x200:                                                      // WM_MOUSEMOVE
                {
                    if (!tracking)
                    {
                        var t = new Native.TRACKMOUSEEVENT { dwFlags = 2 /*TME_LEAVE*/, hwndTrack = Handle };
                        t.cbSize = Marshal.SizeOf(t);
                        tracking = Native.TrackMouseEvent(ref t);
                    }
                    int x = (short)((long)m.LParam & 0xFFFF);
                    int i = ItemAt(x);
                    if (i != hover) { hover = i; Redraw(); }
                    if (i >= 0)
                    {
                        Native.RECT r; Native.GetWindowRect(Handle, out r);
                        var it = items[i];
                        tip.ShowAt(SlotRenderer.Describe(it.R), new Point(r.L + it.X + it.W / 2, r.T));
                    }
                    else tip.Hide();
                    return;
                }
                case 0x2A3: tracking = false; hover = -1; tip.Hide(); Redraw(); return; // WM_MOUSELEAVE
                case 0x202: tip.Hide(); if (LeftClick != null) LeftClick(); return;    // WM_LBUTTONUP
                case 0x205: tip.Hide(); if (RightClick != null) RightClick(); return;  // WM_RBUTTONUP
            }
            base.WndProc(ref m);
        }

        public void Dispose() { tip.Dispose(); if (Handle != IntPtr.Zero) DestroyHandle(); }
    }

    // Dica (nome, % e estado do aparelho) no estilo do Windows 11, acima do painel.
    class TipWindow : Form
    {
        string text = "";
        public TipWindow()
        {
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
            DoubleBuffered = true;
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ExStyle |= 0x08000000 | 0x80 | 0x08 | 0x20; return cp; } // NOACTIVATE | TOOLWINDOW | TOPMOST | TRANSPARENT
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int round = 3; Native.DwmSetWindowAttribute(Handle, 33, ref round, 4); // cantos arredondados pequenos
        }

        Font TipFont() { return new Font("Segoe UI", 12f * Native.Scale(), FontStyle.Regular, GraphicsUnit.Pixel); }

        // anchor: ponto (tela) no topo do item; a dica aparece centralizada acima dele
        public void ShowAt(string s, Point anchor)
        {
            float sc = Native.Scale();
            if (s != text || !Visible)
            {
                text = s;
                using (var f = TipFont())
                {
                    var sz = TextRenderer.MeasureText(text, f);
                    Size = new Size(sz.Width + (int)(16 * sc), sz.Height + (int)(10 * sc));
                }
            }
            var scr = Screen.FromPoint(anchor).WorkingArea;
            int x = Math.Max(scr.Left + 4, Math.Min(anchor.X - Width / 2, scr.Right - Width - 4));
            Location = new Point(x, anchor.Y - Height - (int)(8 * sc));
            Invalidate();
            if (!Visible) Show();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var t = MenuTheme.Current();
            e.Graphics.Clear(t.Bg);
            using (var f = TipFont())
                TextRenderer.DrawText(e.Graphics, text, f, ClientRectangle, t.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }

    // ------------------------------------------------------------------ Janela "Personalizar": estilo, opções do painel e cada aparelho, com prévia ao vivo
    class AppearanceForm : Form
    {
        readonly Appearance look;
        readonly Func<List<Reading>> devices;
        readonly Action changed;
        readonly MenuTheme t = MenuTheme.Current();
        readonly float s = Native.Scale();
        readonly Control preview;
        readonly TableLayoutPanel devTable;
        RadioButton rbPanel, rbIcons;
        readonly List<Control> panelOptions = new List<Control>();

        public AppearanceForm(Appearance look, Func<List<Reading>> devices, Action changed)
        {
            this.look = look; this.devices = devices; this.changed = changed;
            Text = "Personalizar o BatteryTray";
            FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen; AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Font = new Font("Segoe UI", 9.5f);
            try { Icon = Icon.FromHandle(SlotRenderer.AppIcon()); } catch { }

            var root = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(P(18)), Dock = DockStyle.Fill };
            Controls.Add(root);

            preview = new PreviewBox(this) { Height = P(64), Width = P(460), Margin = new Padding(0, 0, 0, P(14)) };
            root.Controls.Add(preview);

            root.Controls.Add(Header("Estilo"));
            rbPanel = new RadioButton { Text = "Painel na barra de tarefas, ao lado da bandeja", AutoSize = true, Checked = look.Panel };
            rbIcons = new RadioButton { Text = "Um ícone por aparelho, dentro da bandeja (modo anterior)", AutoSize = true, Checked = !look.Panel };
            rbPanel.CheckedChanged += (o, e) => { if (rbPanel.Checked) { look.Panel = true; Apply(); } };
            rbIcons.CheckedChanged += (o, e) => { if (rbIcons.Checked) { look.Panel = false; Apply(); } };
            root.Controls.Add(rbPanel); root.Controls.Add(rbIcons);

            root.Controls.Add(Header("Painel"));
            panelOptions.Add(Check(root, "Barra de bateria embaixo da porcentagem", look.ShowBar, v => look.ShowBar = v));
            panelOptions.Add(Check(root, "Símbolo % depois do número", look.ShowPercent, v => look.ShowPercent = v));
            panelOptions.Add(Check(root, "Nomes curtos no ícone (ex.: \"600\")", look.ShowLabels, v => look.ShowLabels = v));
            var sizeRow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, P(4), 0, 0) };
            sizeRow.Controls.Add(new Label { Text = "Tamanho", AutoSize = true, Margin = new Padding(0, P(6), P(8), 0) });
            var sizes = new List<RadioButton>();
            foreach (var name in new[] { "Compacto", "Normal", "Grande" })
            {
                int index = sizes.Count;
                var rb = new RadioButton { Text = name, AutoSize = true, Checked = look.Size == index, Margin = new Padding(0, P(4), P(12), 0) };
                rb.CheckedChanged += (o, e) => { if (rb.Checked) { look.Size = index; Apply(); } };
                sizes.Add(rb); sizeRow.Controls.Add(rb);
            }
            root.Controls.Add(sizeRow); panelOptions.Add(sizeRow);

            root.Controls.Add(Header("Aparelhos"));
            root.Controls.Add(new Label { Text = "Clique na cor para trocar. O nome curto aparece no canto do ícone, e as setas mudam a ordem.", AutoSize = true, MaximumSize = new Size(P(460), 0), Tag = "muted", Margin = new Padding(0, 0, 0, P(6)) });
            devTable = new TableLayoutPanel { ColumnCount = 5, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
            root.Controls.Add(devTable);
            BuildDevices();

            var footer = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, P(16), 0, 0) };
            var close = Btn("Fechar"); close.Click += (o, e) => Close();
            var reset = Btn("Restaurar padrão");
            reset.Click += (o, e) =>
            {
                look.ShowBar = look.ShowPercent = look.ShowLabels = true; look.Size = 1;
                look.Colors.Clear(); look.Labels.Clear(); look.Order.Clear();
                sizes[1].Checked = true;
                foreach (var c in panelOptions.OfType<CheckBox>()) c.Checked = true;
                BuildDevices(); Apply();
            };
            footer.Controls.Add(close); footer.Controls.Add(reset);
            root.Controls.Add(footer);
            AcceptButton = close; CancelButton = close;

            var now = devices().Select(r => new PanelRenderer.Item { R = r }).ToList();
            preview.Width = Math.Max(P(460), PanelRenderer.Layout(now, look, s) + P(24));
            Theme(this);
            UpdateEnabled();
        }

        int P(float v) { return (int)Math.Round(v * s); }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int dark = t.Dark ? 1 : 0; Native.DwmSetWindowAttribute(Handle, 20, ref dark, 4); // barra de título escura
        }

        Label Header(string text)
        {
            return new Label { Text = text, AutoSize = true, Font = new Font("Segoe UI Semibold", 10.5f), Margin = new Padding(0, P(10), 0, P(4)) };
        }

        CheckBox Check(TableLayoutPanel root, string text, bool value, Action<bool> set)
        {
            var c = new CheckBox { Text = text, AutoSize = true, Checked = value };
            c.CheckedChanged += (o, e) => { set(c.Checked); Apply(); };
            root.Controls.Add(c);
            return c;
        }

        Button Btn(string text)
        {
            var b = new Button { Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat, Padding = new Padding(P(8), P(2), P(8), P(2)), Margin = new Padding(P(6), 0, 0, 0) };
            b.FlatAppearance.BorderColor = t.Border;
            return b;
        }

        // botão pequeno com seta (ícone do Windows), para mudar a ordem
        Button Arrow(string glyph, string tip)
        {
            var b = new Button { Text = glyph, Font = new Font("Segoe Fluent Icons", 8f), Width = P(30), Height = P(26), FlatStyle = FlatStyle.Flat, Margin = new Padding(P(4), P(3), 0, P(3)) };
            b.FlatAppearance.BorderColor = t.Border;
            new ToolTip().SetToolTip(b, tip);
            return b;
        }

        // aparelhos na ordem do painel (os sem ordem salva vão para o fim, na ordem padrão)
        List<Reading> OrderedDevices()
        {
            return devices().OrderBy(r => look.OrderOf(r.Key)).ToList();
        }

        void BuildDevices()
        {
            devTable.SuspendLayout();
            devTable.Controls.Clear(); devTable.RowStyles.Clear();
            var list = OrderedDevices();
            if (list.Count == 0) devTable.Controls.Add(new Label { Text = "Nenhum aparelho encontrado ainda.", AutoSize = true, Tag = "muted" });
            for (int i = 0; i < list.Count; i++)
            {
                var r = list[i]; string key = r.Key; int index = i;
                var swatch = new Button { Tag = "swatch", Width = P(26), Height = P(26), FlatStyle = FlatStyle.Flat, BackColor = look.ColorOf(r), Margin = new Padding(0, P(3), P(10), P(3)), Cursor = Cursors.Hand };
                swatch.FlatAppearance.BorderColor = t.Border;
                swatch.Click += (o, e) =>
                {
                    using (var dlg = new ColorDialog { Color = swatch.BackColor, FullOpen = true, AnyColor = true })
                        if (dlg.ShowDialog(this) == DialogResult.OK)
                        {
                            look.Colors[key] = dlg.Color.ToArgb(); swatch.BackColor = dlg.Color; Apply();
                        }
                };
                var name = new Label { Text = r.Name, AutoSize = true, Margin = new Padding(0, P(8), P(12), 0) };
                var label = new TextBox { Text = look.LabelOf(key), MaxLength = 5, Width = P(64), Margin = new Padding(0, P(4), P(8), 0) };
                label.TextChanged += (o, e) => { look.Labels[key] = label.Text.Trim(); Apply(); };
                var up = Arrow("", "Subir"); up.Enabled = i > 0; up.Click += (o, e) => MoveDevice(index, -1);
                var down = Arrow("", "Descer"); down.Enabled = i < list.Count - 1; down.Click += (o, e) => MoveDevice(index, +1);
                devTable.Controls.Add(swatch, 0, i); devTable.Controls.Add(name, 1, i); devTable.Controls.Add(label, 2, i);
                devTable.Controls.Add(up, 3, i); devTable.Controls.Add(down, 4, i);
            }
            Theme(devTable);
            devTable.ResumeLayout();
        }

        void MoveDevice(int index, int delta)
        {
            var keys = OrderedDevices().Select(r => r.Key).ToList();
            int j = index + delta;
            if (j < 0 || j >= keys.Count) return;
            var tmp = keys[index]; keys[index] = keys[j]; keys[j] = tmp;
            foreach (var k in look.Order.Where(k => !keys.Contains(k))) keys.Add(k); // guarda a ordem de aparelhos ausentes agora
            look.Order.Clear(); look.Order.AddRange(keys);
            BuildDevices(); Apply();
        }

        void Apply()
        {
            UpdateEnabled();
            changed();
            preview.Invalidate();
        }

        void UpdateEnabled() { foreach (var c in panelOptions) c.Enabled = look.Panel; }

        // cores do tema do Windows (claro/escuro); amostras de cor ("swatch") ficam com a cor do aparelho
        void Theme(Control c)
        {
            if ("swatch".Equals(c.Tag)) return;
            c.BackColor = c is TextBox || c is ComboBox ? (t.Dark ? Color.FromArgb(32, 32, 32) : Color.White) : c is Button ? t.Hover : t.Bg;
            c.ForeColor = "muted".Equals(c.Tag) ? t.Muted : t.Text;
            foreach (Control child in c.Controls) Theme(child);
        }

        // Prévia: o painel desenhado sobre a cor da barra de tarefas
        class PreviewBox : Control
        {
            readonly AppearanceForm f;
            public PreviewBox(AppearanceForm f) { this.f = f; DoubleBuffered = true; }
            protected override void OnPaint(PaintEventArgs e)
            {
                bool light = SlotRenderer.LightTheme();
                e.Graphics.Clear(light ? Color.FromArgb(238, 238, 238) : Color.FromArgb(28, 28, 28));
                if (!f.look.Panel)
                {
                    TextRenderer.DrawText(e.Graphics, "Modo de ícones na bandeja: cada aparelho é um ícone que você fixa na barra.", Font, ClientRectangle,
                        light ? Color.FromArgb(80, 80, 80) : Color.FromArgb(180, 180, 180), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
                    return;
                }
                var items = f.OrderedDevices().Select(r => new PanelRenderer.Item { R = r, Color = f.look.ColorOf(r), Label = f.look.LabelOf(r.Key) }).ToList();
                if (items.Count == 0) return;
                int h = (int)(48 * f.s), w = PanelRenderer.Layout(items, f.look, f.s);
                using (var b = PanelRenderer.Render(items, f.look, f.s, w, h, light, -1))
                    e.Graphics.DrawImage(b, Math.Max(0, (Width - w) / 2), (Height - h) / 2);
            }
        }
    }

    // ------------------------------------------------------------------ Um ícone de bandeja por aparelho (é ele que o usuário fixa e arrasta)
    class TrayIcons : NativeWindow, IDisposable
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct NOTIFYICONDATA
        {
            public int cbSize; public IntPtr hWnd; public int uID; public int uFlags; public int uCallbackMessage; public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
            public int dwState, dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
            public int uVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
            public int dwInfoFlags; public Guid guidItem; public IntPtr hBalloonIcon;
        }
        [StructLayout(LayoutKind.Sequential)] struct NOTIFYICONIDENTIFIER { public int cbSize; public IntPtr hWnd; public int uID; public Guid guidItem; }
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern bool Shell_NotifyIcon(int msg, ref NOTIFYICONDATA d);
        [DllImport("shell32.dll")] static extern int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER id, out Native.RECT r);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int RegisterWindowMessage(string s);

        const int NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
        const int NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_SHOWTIP = 0x80;
        const int WM_TRAY = 0x8001, FirstUid = 100;
        const string UidKey = @"Software\BatteryTray\Uids";

        class Entry { public int Uid; public IntPtr Icon; public string Tip; public bool Added; }
        readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>();
        readonly int taskbarCreated;
        public readonly IntPtr Blank;
        public Action LeftClick, RightClick, Recreated, NotificationClick;

        public TrayIcons()
        {
            CreateHandle(new CreateParams { Caption = "BatteryTraySlots" }); // janela oculta (recebe TaskbarCreated)
            taskbarCreated = RegisterWindowMessage("TaskbarCreated");
            using (var b = new Bitmap(16, 16, PixelFormat.Format32bppArgb)) Blank = b.GetHicon();
        }

        // Chave de um aparelho no formato de uma versão anterior -> chave atual (a própria chave, se não mudou).
        //  - Logitech até a v1.3.2: "logi:<caminho USB do receptor>#<n>" -> "logi:<PID do receptor>#<n>"
        //  - GameSir até a v1.3.1: o nome do aparelho -> "gamesir:3537"
        public static string CurrentKey(string key)
        {
            var m = Regex.Match(key, @"^logi:\\\\\?\\hid#vid_046d&pid_([0-9a-f]{4})[^#]*#.*#(\d+)$", RegexOptions.IgnoreCase);
            if (m.Success) return "logi:" + m.Groups[1].Value.ToUpperInvariant() + "#" + m.Groups[2].Value;
            if (key == "gamesir:xbox 360 controller for windows" || key == "gamesir:gamesir cyclone 2") return "gamesir:3537";
            return key;
        }

        // Cada aparelho tem um uID fixo (salvo no registro), para o Windows lembrar que ele está fixado.
        int UidFor(string key)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(UidKey))
            {
                object v = k.GetValue(key);
                if (v is int) return (int)v;
                // aparelho que mudou de chave numa versão nova: herda o uID antigo (de preferência o fixado na barra),
                // para o usuário não precisar fixar o ícone de novo
                {
                    var olds = k.GetValueNames().Where(n => n != key && CurrentKey(n) == key).Select(o => k.GetValue(o)).OfType<int>().ToList();
                    if (olds.Count > 0)
                    {
                        var pinned = PinnedUids();
                        int keep = olds.FirstOrDefault(pinned.Contains);
                        if (keep == 0) keep = olds[0];
                        k.SetValue(key, keep, RegistryValueKind.DWord); // as chaves antigas ficam, para o uID não ser reaproveitado
                        return keep;
                    }
                }
                var used = new HashSet<int>(k.GetValueNames().Select(n => k.GetValue(n)).OfType<int>());
                int uid = FirstUid;
                while (used.Contains(uid)) uid++;
                k.SetValue(key, uid, RegistryValueKind.DWord);
                return uid;
            }
        }

        NOTIFYICONDATA Data(Entry e)
        {
            var d = new NOTIFYICONDATA();
            d.cbSize = Marshal.SizeOf(d); d.hWnd = Handle; d.uID = e.Uid;
            d.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP; d.uCallbackMessage = WM_TRAY;
            d.hIcon = e.Icon; d.szTip = e.Tip.Length > 127 ? e.Tip.Substring(0, 127) : e.Tip;
            return d;
        }

        // Cria/atualiza o ícone do aparelho. "icon" passa a pertencer a esta classe.
        public int Set(string key, IntPtr icon, string tip)
        {
            Entry e;
            if (!entries.TryGetValue(key, out e)) entries[key] = e = new Entry { Uid = UidFor(key) };
            IntPtr oldIcon = e.Icon;
            e.Icon = icon; e.Tip = tip;
            var d = Data(e);
            if (!e.Added)
            {
                e.Added = Shell_NotifyIcon(NIM_ADD, ref d);
                d.uVersion = 4; Shell_NotifyIcon(NIM_SETVERSION, ref d);
            }
            else Shell_NotifyIcon(NIM_MODIFY, ref d);
            if (oldIcon != IntPtr.Zero && oldIcon != Blank && oldIcon != icon && !Shared(oldIcon)) Native.DestroyIcon(oldIcon);
            return e.Uid;
        }

        public bool Has(string key) { Entry e; return entries.TryGetValue(key, out e) && e.Added; }

        // Notificação do Windows saindo do ícone do aparelho (respeita o "Não incomodar").
        public void Notify(string key, string title, string text)
        {
            Entry e;
            if (!entries.TryGetValue(key, out e) || !e.Added) return;
            var d = Data(e);
            d.uFlags |= 0x10; // NIF_INFO
            d.szInfoTitle = title.Length > 63 ? title.Substring(0, 63) : title;
            d.szInfo = text.Length > 255 ? text.Substring(0, 255) : text;
            d.dwInfoFlags = 0x2 | 0x80; // NIIF_WARNING | NIIF_RESPECT_QUIET_TIME
            Shell_NotifyIcon(NIM_MODIFY, ref d);
        }

        // Ícones reaproveitados (ex.: o do app) não são destruídos ao trocar.
        public readonly HashSet<IntPtr> SharedIcons = new HashSet<IntPtr>();
        bool Shared(IntPtr h) { return SharedIcons.Contains(h); }

        // Tira o ícone da bandeja (o uID continua reservado, então a fixação volta quando ele reaparecer).
        public void Remove(string key)
        {
            Entry e;
            if (!entries.TryGetValue(key, out e) || !e.Added) return;
            var d = Data(e); Shell_NotifyIcon(NIM_DELETE, ref d);
            e.Added = false;
        }
        public int UidOf(string key) { Entry e; return entries.TryGetValue(key, out e) ? e.Uid : UidFor(key); }

        public Rectangle? Rect(int uid)
        {
            var id = new NOTIFYICONIDENTIFIER(); id.cbSize = Marshal.SizeOf(id); id.hWnd = Handle; id.uID = uid;
            Native.RECT r;
            if (Shell_NotifyIconGetRect(ref id, out r) != 0) return null;
            return Rectangle.FromLTRB(r.L, r.T, r.R, r.B);
        }

        // uIDs que o usuário fixou na barra (o Explorer grava isso quando o ícone é arrastado/ligado).
        public HashSet<int> PinnedUids()
        {
            var res = new HashSet<int>();
            try
            {
                using (var root = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings"))
                {
                    if (root == null) return res;
                    foreach (var name in root.GetSubKeyNames())
                        using (var k = root.OpenSubKey(name))
                        {
                            if (k == null) continue;
                            var path = k.GetValue("ExecutablePath") as string;
                            if (path == null || !string.Equals(path, Application.ExecutablePath, StringComparison.OrdinalIgnoreCase)) continue;
                            object uid = k.GetValue("UID"), p = k.GetValue("IsPromoted");
                            if (uid is int && p is int && (int)p == 1) res.Add((int)uid);
                        }
                }
            }
            catch { }
            return res;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == taskbarCreated && taskbarCreated != 0)
            {
                foreach (var e in entries.Values) e.Added = false; // Explorer reiniciou: recria tudo
                if (Recreated != null) Recreated();
            }
            else if (m.Msg == WM_TRAY)
            {
                int ev = (int)((long)m.LParam & 0xFFFF);
                if (ev == 0x202 && LeftClick != null) LeftClick();                       // WM_LBUTTONUP
                else if ((ev == 0x205 || ev == 0x7B) && RightClick != null) RightClick(); // WM_RBUTTONUP / WM_CONTEXTMENU
                else if (ev == 0x405 && NotificationClick != null) NotificationClick();   // NIN_BALLOONUSERCLICK
            }
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            foreach (var e in entries.Values)
            {
                if (e.Added) { var d = Data(e); Shell_NotifyIcon(NIM_DELETE, ref d); }
                if (e.Icon != IntPtr.Zero && e.Icon != Blank) Native.DestroyIcon(e.Icon);
            }
            Native.DestroyIcon(Blank);
            if (Handle != IntPtr.Zero) DestroyHandle();
        }
    }

    // ------------------------------------------------------------------ Menu no estilo do Windows 11
    class MenuTheme
    {
        public bool Dark;
        public Color Bg, Hover, Text, Muted, Separator, Accent, OnAccent, Border, Low;

        public static MenuTheme Current()
        {
            var t = new MenuTheme { Dark = !SlotRenderer.LightTheme() };
            if (t.Dark)
            {
                t.Bg = Color.FromArgb(44, 44, 44); t.Hover = Color.FromArgb(61, 61, 61); t.Text = Color.White;
                t.Muted = Color.FromArgb(170, 170, 170); t.Separator = Color.FromArgb(64, 64, 64); t.Border = Color.FromArgb(70, 70, 70);
                t.OnAccent = Color.Black; t.Low = Color.FromArgb(255, 110, 110);
            }
            else
            {
                t.Bg = Color.FromArgb(249, 249, 249); t.Hover = Color.FromArgb(234, 234, 234); t.Text = Color.FromArgb(26, 26, 26);
                t.Muted = Color.FromArgb(105, 105, 105); t.Separator = Color.FromArgb(225, 225, 225); t.Border = Color.FromArgb(215, 215, 215);
                t.OnAccent = Color.White; t.Low = Color.FromArgb(196, 43, 28);
            }
            t.Accent = AccentColor(t.Dark);
            return t;
        }

        // Cor de destaque do Windows (a mesma dos interruptores das Configurações).
        static Color AccentColor(bool dark)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent"))
                {
                    var pal = k == null ? null : k.GetValue("AccentPalette") as byte[];
                    if (pal != null && pal.Length >= 32) { int i = (dark ? 1 : 4) * 4; return Color.FromArgb(pal[i], pal[i + 1], pal[i + 2]); }
                    object v = k == null ? null : k.GetValue("AccentColorMenu");
                    if (v is int) { int c = (int)v; return Color.FromArgb(c & 0xFF, (c >> 8) & 0xFF, (c >> 16) & 0xFF); }
                }
            }
            catch { }
            return dark ? Color.FromArgb(76, 194, 255) : Color.FromArgb(0, 95, 184);
        }
    }

    // ToolStripDropDown (e não ContextMenuStrip): respeita a largura definida em cada item.
    class ModernMenu : ToolStripDropDown
    {
        public readonly MenuTheme T = MenuTheme.Current();
        public readonly float S = Native.Scale();
        public const int MenuWidth = 236;
        // O menu só fecha quando o próprio app manda (clique fora, Esc, "Sair").
        // Assim, clicar nos interruptores ou a bandeja trocar de ícones não o fecha.
        bool allowClose;
        bool wasDown = true;
        readonly System.Windows.Forms.Timer watch = new System.Windows.Forms.Timer { Interval = 40 };

        public void Hold() { } // mantido por compatibilidade com os itens

        // Animação dos interruptores (~60 fps enquanto algum estiver em transição).
        const float SwitchMs = 150f;
        readonly HashSet<FancyItem> animating = new HashSet<FancyItem>();
        readonly System.Windows.Forms.Timer animTimer = new System.Windows.Forms.Timer { Interval = 15 };
        readonly System.Diagnostics.Stopwatch animClock = new System.Diagnostics.Stopwatch();

        public void Animate(FancyItem item)
        {
            if (animating.Add(item) && !animTimer.Enabled) { animClock.Restart(); animTimer.Start(); }
        }

        void AnimTick()
        {
            float delta = (float)animClock.Elapsed.TotalMilliseconds / SwitchMs;
            animClock.Restart();
            foreach (var it in animating.ToList()) if (it.Step(delta)) animating.Remove(it);
            if (animating.Count == 0) animTimer.Stop();
        }

        public void Dismiss()
        {
            if (IsDisposed) return;
            allowClose = true;
            Close(ToolStripDropDownCloseReason.CloseCalled);
        }

        public ModernMenu()
        {
            Renderer = new ModernRenderer(this);
            LayoutStyle = ToolStripLayoutStyle.VerticalStackWithOverflow;
            AutoSize = true;
            AutoClose = false;
            animTimer.Tick += (s, e) => AnimTick();
            watch.Tick += (s, e) =>
            {
                // fecha ao clicar fora do menu (em qualquer lugar da tela)
                bool down = Control.MouseButtons != MouseButtons.None;
                if (down && !wasDown && !Bounds.Contains(Cursor.Position)) Dismiss();
                wasDown = down;
            };
            DropShadowEnabled = false; // a sombra vem do próprio Windows (cantos arredondados)
            BackColor = T.Bg;
            Padding = new Padding(0, (int)(4 * S), 0, (int)(4 * S));
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int round = 2; Native.DwmSetWindowAttribute(Handle, 33, ref round, 4);             // DWMWA_WINDOW_CORNER_PREFERENCE = arredondado
            int border = ColorTranslator.ToWin32(T.Border); Native.DwmSetWindowAttribute(Handle, 34, ref border, 4); // DWMWA_BORDER_COLOR
        }

        protected override void OnOpened(EventArgs e) { base.OnOpened(e); wasDown = true; watch.Start(); }
        protected override void OnClosed(ToolStripDropDownClosedEventArgs e) { watch.Stop(); base.OnClosed(e); }

        protected override void OnClosing(ToolStripDropDownClosingEventArgs e)
        {
            if (e.CloseReason == ToolStripDropDownCloseReason.Keyboard) allowClose = true; // Esc
            if (!allowClose) e.Cancel = true;
            base.OnClosing(e);
        }

        protected override bool ProcessDialogKey(Keys keyData)
        {
            if (keyData == Keys.Escape) { Dismiss(); return true; }
            return base.ProcessDialogKey(keyData);
        }

        protected override void Dispose(bool disposing) { if (disposing) { watch.Dispose(); animTimer.Dispose(); } base.Dispose(disposing); }

        public Font UiFont(float px, bool semibold = false)
        {
            string fam = semibold ? "Segoe UI Variable Text Semibold" : "Segoe UI Variable Text";
            var f = new Font(fam, px * S, FontStyle.Regular, GraphicsUnit.Pixel);
            if (f.Name != fam) { f.Dispose(); f = new Font("Segoe UI", px * S, semibold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel); }
            return f;
        }

        public Font IconFont(float px) { return new Font("Segoe Fluent Icons", px * S, FontStyle.Regular, GraphicsUnit.Pixel); }

        public void AddSeparator() { Items.Add(new ToolStripSeparator { AutoSize = false, Size = new Size((int)(MenuWidth * S), (int)(7 * S)) }); }
    }

    class ModernRenderer : ToolStripRenderer
    {
        readonly ModernMenu m;
        public ModernRenderer(ModernMenu menu) { m = menu; }
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) { using (var b = new SolidBrush(m.T.Bg)) e.Graphics.FillRectangle(b, e.AffectedBounds); }
        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e) { }
        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int y = e.Item.Height / 2;
            using (var p = new Pen(m.T.Separator)) e.Graphics.DrawLine(p, 0, y, e.Item.Width, y);
        }
    }

    // Base dos itens desenhados à mão: fundo de "hover" arredondado e utilitários de desenho.
    abstract class FancyItem : ToolStripMenuItem
    {
        protected ModernMenu M { get { return (ModernMenu)Owner; } }
        readonly Size fixedSize;
        protected FancyItem(int height, float s) { AutoSize = false; fixedSize = new Size((int)(ModernMenu.MenuWidth * s), (int)(height * s)); Size = fixedSize; }
        public override Size GetPreferredSize(Size constrainingSize) { return fixedSize; } // o menu usa isto para calcular a largura

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; var t = M.T; float s = M.S;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var b = new SolidBrush(t.Bg)) g.FillRectangle(b, 0, 0, Width, Height);
            if (Selected && Enabled)
                using (var b = new SolidBrush(t.Hover))
                using (var p = Round(new RectangleF(4 * s, 1 * s, Width - 8 * s, Height - 2 * s), 4 * s))
                    g.FillPath(b, p);
            Draw(g, t, s);
        }

        protected abstract void Draw(Graphics g, MenuTheme t, float s);

        protected static GraphicsPath Round(RectangleF r, float rad)
        {
            var p = new GraphicsPath(); float d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure(); return p;
        }

        protected void DrawLabel(Graphics g, string text, Font f, Color c, float x, float y, float w)
        {
            TextRenderer.DrawText(g, text, f, new Rectangle((int)x, (int)y, (int)w, (int)(f.Height + 4)), c,
                TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        }

        protected void Glyph(Graphics g, string glyph, float px, Color c, float cx, float cy)
        {
            using (var f = M.IconFont(px))
            {
                var sz = TextRenderer.MeasureText(g, glyph, f, Size.Empty, TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, glyph, f, new Point((int)(cx - sz.Width / 2f), (int)(cy - sz.Height / 2f)), c, TextFormatFlags.NoPadding);
            }
        }

        // Interruptor igual ao das Configurações do Windows, com transição suave
        // (a bolinha desliza e a cor vai do contorno ao preenchido em ~150 ms).
        float switchPos = -1, switchTarget; // 0 = desligado, 1 = ligado

        protected void Switch(Graphics g, MenuTheme t, float s, bool on, float right, float cy)
        {
            switchTarget = on ? 1 : 0;
            if (switchPos < 0) switchPos = switchTarget;           // primeira vez: sem animação
            if (switchPos != switchTarget) M.Animate(this);
            float e = Ease(switchPos);

            float w = 32 * s, h = 16 * s, x = right - w, y = cy - h / 2;
            using (var path = Round(new RectangleF(x, y, w, h), h / 2))
            {
                if (e > 0) using (var b = new SolidBrush(Color.FromArgb((int)(255 * e), t.Accent))) g.FillPath(b, path);
                using (var p = new Pen(Blend(t.Muted, t.Accent, e), 1 * s)) g.DrawPath(p, path);
            }
            float k = (9 + 2 * e) * s;
            float kx = (x + h / 2) + (w - h) * e - k / 2;
            using (var b = new SolidBrush(Blend(t.Muted, t.OnAccent, e))) g.FillEllipse(b, kx, cy - k / 2, k, k);
        }

        // Avança a animação; devolve true quando chegou ao fim.
        public bool Step(float delta)
        {
            switchPos = switchPos < switchTarget ? Math.Min(switchTarget, switchPos + delta) : Math.Max(switchTarget, switchPos - delta);
            Invalidate();
            return switchPos == switchTarget;
        }

        static float Ease(float p) { return p < 0.5f ? 2 * p * p : 1 - (float)Math.Pow(-2 * p + 2, 2) / 2; }

        static Color Blend(Color a, Color b, float e)
        {
            return Color.FromArgb((int)(a.R + (b.R - a.R) * e), (int)(a.G + (b.G - a.G) * e), (int)(a.B + (b.B - a.B) * e));
        }
    }

    class HeaderItem : FancyItem
    {
        readonly string text;
        public HeaderItem(string text, float s) : base(26, s) { this.text = text; Enabled = false; }
        protected override void Draw(Graphics g, MenuTheme t, float s)
        {
            using (var f = M.UiFont(11, true)) DrawLabel(g, text, f, t.Muted, 14 * s, 8 * s, Width - 28 * s);
        }
    }

    class DeviceItem : FancyItem
    {
        readonly Func<Reading> reading; readonly Func<bool> visible; readonly Action toggle;
        public DeviceItem(Func<Reading> reading, Func<bool> visible, Action toggle, float s) : base(40, s)
        { this.reading = reading; this.visible = visible; this.toggle = toggle; }

        protected override void Draw(Graphics g, MenuTheme t, float s)
        {
            var r = reading(); if (r == null) return;
            bool on = visible();
            float cy = Height / 2f;
            Glyph(g, SlotRenderer.Glyph(r.Kind), 16, on ? t.Text : t.Muted, 24 * s, cy);
            using (var f = M.UiFont(12.5f)) DrawLabel(g, r.Name, f, on ? t.Text : t.Muted, 44 * s, cy - 15 * s, Width - 100 * s);
            string pct = r.Percent < 0 ? "?" : (r.LevelText != null ? "~" : "") + r.Percent + "%";
            string status = !r.Online ? (r.Updated > DateTime.MinValue ? "desligado · " + SlotRenderer.Ago(r.Updated) : "desligado")
                          : r.Charging ? "carregando" : (r.LevelText != null ? "estimada" : null);
            Color pc = r.Online && r.Percent >= 0 && r.Percent <= 15 && !r.Charging ? t.Low : t.Muted;
            using (var f = M.UiFont(11)) DrawLabel(g, status == null ? pct : pct + " · " + status, f, pc, 44 * s, cy + 1 * s, Width - 100 * s);
            Switch(g, t, s, on, Width - 12 * s, cy);
        }

        protected override void OnClick(EventArgs e) { M.Hold(); toggle(); Invalidate(); base.OnClick(e); M.Hold(); }
    }

    class ActionItem : FancyItem
    {
        readonly string glyph, text; readonly Action action; readonly Func<bool> state;
        public ActionItem(string glyph, string text, Action action, Func<bool> state, float s) : base(30, s)
        { this.glyph = glyph; this.text = text; this.action = action; this.state = state; }

        protected override void Draw(Graphics g, MenuTheme t, float s)
        {
            float cy = Height / 2f;
            Glyph(g, glyph, 14, t.Text, 24 * s, cy);
            using (var f = M.UiFont(12.5f)) DrawLabel(g, text, f, t.Text, 44 * s, cy - f.Height / 2f - 1 * s, Width - 100 * s);
            if (state != null) Switch(g, t, s, state(), Width - 12 * s, cy);
        }

        protected override void OnClick(EventArgs e)
        {
            var menu = M;
            if (state == null) menu.Dismiss(); // ações comuns fecham o menu; interruptores não
            action(); Invalidate(); base.OnClick(e);
        }
    }

    // ------------------------------------------------------------------ Atualização pelo GitHub Releases
    static class Updater
    {
        // Ao publicar uma versão nova, mude este número e crie a tag "v" + número (veja release.ps1).
        public const string Version = "1.5.1";
        const string Repo = "HenriqueGomez/BatteryTray";
        const string Agent = "BatteryTray-Updater";

        public class Release { public string Tag; public Version Ver; public string ExeUrl, ShaUrl, PageUrl; }

        static string Get(string url)
        {
            System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;
            var req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(url);
            req.UserAgent = Agent; req.Accept = "application/vnd.github+json"; req.Timeout = 15000;
            using (var resp = req.GetResponse()) using (var sr = new StreamReader(resp.GetResponseStream())) return sr.ReadToEnd();
        }

        // Devolve a versão publicada mais nova, se for maior que a atual; senão null.
        public static Release Check()
        {
            string json = Get("https://api.github.com/repos/" + Repo + "/releases/latest");
            var tag = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"([^\"]+)\"").Groups[1].Value;
            Version v;
            if (!System.Version.TryParse(tag.TrimStart('v', 'V'), out v)) return null;
            var r = new Release { Tag = tag, Ver = v, PageUrl = Regex.Match(json, "\"html_url\"\\s*:\\s*\"([^\"]+/releases/tag/[^\"]+)\"").Groups[1].Value };
            foreach (Match m in Regex.Matches(json, "\"browser_download_url\"\\s*:\\s*\"([^\"]+)\""))
            {
                string url = m.Groups[1].Value;
                if (url.EndsWith("/BatteryTray.exe", StringComparison.OrdinalIgnoreCase)) r.ExeUrl = url;
                else if (url.EndsWith("/BatteryTray.exe.sha256", StringComparison.OrdinalIgnoreCase)) r.ShaUrl = url;
            }
            if (r.ExeUrl == null || r.ShaUrl == null) return null;
            return v > new Version(Version) ? r : null;
        }

        // Baixa, confere o SHA-256, troca o .exe (o Windows deixa renomear o arquivo em uso) e, se pedido, reabre.
        public static void Apply(Release r, bool restart)
        {
            string exe = Application.ExecutablePath, dir = Path.GetDirectoryName(exe);
            string tmp = Path.Combine(dir, "BatteryTray.new.exe"), old = Path.Combine(dir, "BatteryTray.old.exe");
            System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;
            using (var wc = new System.Net.WebClient())
            {
                wc.Headers.Add("User-Agent", Agent);
                string expected = wc.DownloadString(r.ShaUrl).Trim().Split(' ', '\t', '\r', '\n')[0].ToLowerInvariant();
                wc.Headers.Add("User-Agent", Agent);
                wc.DownloadFile(r.ExeUrl, tmp);
                string actual;
                using (var sha = System.Security.Cryptography.SHA256.Create())
                using (var fs = File.OpenRead(tmp))
                    actual = BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
                if (expected.Length != 64 || actual != expected) { File.Delete(tmp); throw new InvalidDataException("o arquivo baixado não confere com o SHA-256 publicado"); }
            }
            if (File.Exists(old)) File.Delete(old);
            File.Move(exe, old);
            try { File.Move(tmp, exe); }
            catch { File.Move(old, exe); throw; } // desfaz se algo der errado
            if (restart) System.Diagnostics.Process.Start(exe, "--updated");
        }

        // Apaga o .exe antigo deixado pela última atualização.
        public static void CleanupOld()
        {
            string old = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "BatteryTray.old.exe");
            for (int i = 0; i < 20 && File.Exists(old); i++) { try { File.Delete(old); } catch { Thread.Sleep(250); } }
        }
    }

    // Item do menu: verificar / baixar atualização (fica aberto enquanto verifica).
    class UpdateItem : FancyItem
    {
        readonly Func<Updater.Release> available; readonly Action check, apply; readonly Func<string> status;
        public UpdateItem(Func<Updater.Release> available, Func<string> status, Action check, Action apply, float s) : base(30, s)
        { this.available = available; this.status = status; this.check = check; this.apply = apply; }

        protected override void Draw(Graphics g, MenuTheme t, float s)
        {
            float cy = Height / 2f;
            var rel = available();
            string st = status();
            string label = st ?? (rel != null ? "Atualizar para " + rel.Tag : "Verificar atualizações");
            Color c = rel != null && st == null ? t.Accent : t.Text;
            Glyph(g, rel != null ? "" : "", 14, c, 24 * s, cy);
            using (var f = M.UiFont(12.5f)) DrawLabel(g, label, f, c, 44 * s, cy - f.Height / 2f - 1 * s, Width - 100 * s);
            using (var f = M.UiFont(11))
            {
                string v = "v" + Updater.Version;
                var sz = TextRenderer.MeasureText(g, v, f, Size.Empty, TextFormatFlags.NoPadding);
                DrawLabel(g, v, f, t.Muted, Width - 14 * s - sz.Width, cy - f.Height / 2f, sz.Width + 2);
            }
        }

        protected override void OnClick(EventArgs e)
        {
            if (available() != null) apply(); else check(); // o menu continua aberto e mostra o resultado
            Invalidate(); base.OnClick(e);
        }
    }

    // ------------------------------------------------------------------ Diagnóstico (arquivo de texto para anexar em uma issue)
    // Lista tudo que o app enxerga: leituras de cada fonte, aparelhos USB/HID, Bluetooth, XInput e a conversa HID++.
    // Não inclui números de série, endereços Bluetooth nem caminhos completos dos aparelhos.
    static class Diagnostics
    {
        // Respostas brutas das fontes de protocolo próprio (só enquanto o diagnóstico roda), para conferir os protocolos.
        static StringBuilder raw;
        public static void Raw(string label, byte[] frame, int count)
        {
            var sb = raw;
            if (sb == null) return;
            lock (sb)
            {
                sb.Append(label).Append(": ");
                if (frame == null) sb.AppendLine(Hid.LastError ?? "sem resposta");
                else { for (int i = 0; i < Math.Min(count, frame.Length); i++) sb.AppendFormat("{0:X2} ", frame[i]); sb.AppendLine(); }
            }
        }

        static readonly Dictionary<ushort, string> Vendors = new Dictionary<ushort, string> {
            { 0x046D, "Logitech" }, { 0x1532, "Razer" }, { 0x045E, "Microsoft" }, { 0x054C, "Sony" }, { 0x057E, "Nintendo" },
            { 0x2DC8, "8BitDo" }, { 0x1038, "SteelSeries" }, { 0x1B1C, "Corsair" }, { 0x0951, "HyperX" }, { 0x03F0, "HP/HyperX" },
            { 0x3434, "Keychron" }, { 0x0B05, "ASUS" }, { 0x1E7D, "ROCCAT/Turtle Beach" }, { 0x10F5, "Turtle Beach" }, { 0x1395, "EPOS/Sennheiser" },
            { 0x0D8C, "C-Media (áudio USB)" }, { 0x046A, "Cherry" }, { 0x258A, "Sinowealth (mouses/teclados genéricos)" }, { 0x093A, "Pixart" },
            { 0x248A, "Maxxter/Telink" }, { 0x25A7, "Areson" }, { 0x3554, "Compx (mouses genéricos)" }, { 0x0E8D, "MediaTek" }, { 0x8087, "Intel" },
            { 0x0BDA, "Realtek" }, { 0x05AC, "Apple" }, { 0x2717, "Xiaomi" }, { 0x04E8, "Samsung" }, { 0x1A86, "QinHeng" }, { 0x0483, "STMicroelectronics" },
        };

        static readonly string[,] Programs = {
            { "Logitech G HUB", "lghub_agent,lghub" }, { "Logi Options+", "logioptionsplus_agent" },
            { "Razer Synapse 4", "RazerAppEngine" }, { "Razer Synapse 3", "Razer Synapse 3,Razer Synapse Service" },
            { "SteelSeries GG", "SteelSeriesGG" }, { "Corsair iCUE", "iCUE" },
        };

        static string Usage(Hid.Info c)
        {
            if (c.Vid == 0x046D && c.UsagePage == 0xFF00 && c.Usage == 1) return "HID++ curto (Logitech)";
            if (c.Vid == 0x046D && c.UsagePage == 0xFF00 && c.Usage == 2) return "HID++ longo (Logitech)";
            if (c.Vid == 0x046D && c.UsagePage == 0xFF43) return "HID++ (Logitech G)";
            if (c.UsagePage == 1)
                switch (c.Usage) { case 1: return "ponteiro"; case 2: return "mouse"; case 4: return "joystick"; case 5: return "gamepad"; case 6: return "teclado"; case 0x80: return "controle do sistema"; }
            if (c.UsagePage == 0x0C) return "teclas de mídia";
            if (c.UsagePage == 0x0B) return "telefonia (botões de headset)";
            if (c.UsagePage == 0x0D) return "caneta/toque";
            if (c.UsagePage == 0x84 || c.UsagePage == 0x85) return "bateria/energia";
            if (c.UsagePage >= 0xFF00) return "do fabricante";
            return "outro";
        }

        static string Kind(DeviceKind k)
        {
            switch (k) { case DeviceKind.Mouse: return "Mouse"; case DeviceKind.Headset: return "Headset"; case DeviceKind.Keyboard: return "Teclado"; case DeviceKind.Controller: return "Controle"; default: return "Outro"; }
        }

        static string Source(string key)
        {
            string p = key == null ? "" : key.Split(':')[0];
            switch (p) { case "logi": return "Logitech HID++"; case "razer": return "Razer Synapse"; case "razerhid": return "Razer USB"; case "win": return "Windows/Bluetooth"; case "hid": return "HID genérico"; case "gamesir": return "GameSir"; case "beken": return "Receptor 2.4G"; case "mchose": return "MCHOSE"; case "xinput": return "XInput"; default: return p; }
        }

        public static string Describe(Reading r)
        {
            string pct = r.Percent < 0 ? "bateria ?" : (r.LevelText != null ? "~" : "") + r.Percent + "%";
            return string.Format("{0,-9} {1} · {2} · {3}{4} · fonte: {5}{6}", Kind(r.Kind), r.Name ?? "(sem nome)", pct, r.Online ? "ligado" : "desligado",
                r.Charging ? " · carregando" : "", Source(r.Key), r.Updated > DateTime.MinValue ? " · lido " + r.Updated.ToString("dd/MM HH:mm") : "");
        }

        static string WindowsVersion()
        {
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    string name = (k.GetValue("ProductName") ?? "Windows").ToString(), disp = (k.GetValue("DisplayVersion") ?? "").ToString();
                    string build = (k.GetValue("CurrentBuild") ?? "?").ToString(), ubr = (k.GetValue("UBR") ?? "").ToString();
                    int b; if (int.TryParse(build, out b) && b >= 22000) name = name.Replace("Windows 10", "Windows 11"); // o registro ainda diz "10" no 11
                    return string.Format("{0} {1} (build {2}{3}), {4}", name, disp, build, ubr == "" ? "" : "." + ubr, Environment.Is64BitOperatingSystem ? "64 bits" : "32 bits").Replace("  ", " ");
                }
            }
            catch { return Environment.OSVersion.ToString(); }
        }

        static string DotNetVersion()
        {
            try { using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full")) return "release " + k.GetValue("Release"); }
            catch { return Environment.Version.ToString(); }
        }

        static void Section(StringBuilder sb, string title)
        {
            sb.AppendLine().AppendLine(title).AppendLine(new string('-', title.Length));
        }

        // Pasta do .exe; se não der para gravar lá (ex.: Arquivos de Programas), a Área de Trabalho.
        public static string Write(IList<string> shownNow)
        {
            string text = Build(shownNow);
            string file = "BatteryTray-diagnostico-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt";
            foreach (var dir in new[] { Path.GetDirectoryName(Application.ExecutablePath), Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory) })
            {
                try { string p = Path.Combine(dir, file); File.WriteAllText(p, text, new UTF8Encoding(true)); return p; }
                catch (UnauthorizedAccessException) { }
                catch (IOException) { }
            }
            throw new IOException("não foi possível gravar o arquivo de diagnóstico");
        }

        static string Build(IList<string> shownNow)
        {
            var sb = new StringBuilder();
            sb.AppendLine("BatteryTray - diagnóstico de dispositivos");
            sb.AppendLine("=========================================");
            sb.AppendLine("Gerado em:  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("BatteryTray: v" + Updater.Version);
            sb.AppendLine("Windows:    " + WindowsVersion());
            sb.AppendLine(".NET:       " + DotNetVersion());
            sb.AppendLine();
            sb.AppendLine("Este arquivo ajuda a descobrir por que um aparelho não aparece ou mostra a bateria errada.");
            sb.AppendLine("Para pedir ajuda, abra uma issue em https://github.com/HenriqueGomez/BatteryTray/issues e anexe este arquivo.");
            sb.AppendLine("Ele não tem números de série, endereços Bluetooth nem caminhos completos dos aparelhos, mas mostra os nomes");
            sb.AppendLine("deles. Alguns nomes Bluetooth incluem o nome do dono (ex.: \"Fone do Fulano\"): revise antes de compartilhar.");

            if (shownNow != null)
            {
                Section(sb, "1. O que o BatteryTray mostra agora");
                if (shownNow.Count == 0) sb.AppendLine("(nenhum aparelho)");
                foreach (var l in shownNow) sb.AppendLine(l);
            }

            // leitura nova de cada fonte, com a conversa HID++ gravada num arquivo temporário
            Section(sb, "2. Leitura nova, fonte por fonte");
            string hppLog = Path.Combine(Path.GetTempPath(), "BatteryTray-hpp-" + Guid.NewGuid().ToString("N") + ".log");
            string prevLog = HppChannel.DebugLog;
            HppChannel.DebugLog = hppLog;
            var rawLog = new StringBuilder();
            raw = rawLog;
            var sources = new Dictionary<string, IBatterySource> {
                { "Logitech HID++", new LogitechSource() }, { "Razer Synapse 4 (logs)", new RazerLogSource() }, { "Razer USB (mouses/teclados)", new RazerHidSource() },
                { "Windows/Bluetooth", new WindowsBatterySource() }, { "GameSir", new GameSirSource() }, { "Receptor 2.4G (Beken/CompX)", new BekenMouseSource() }, { "MCHOSE", new MchoseHeadsetSource() }, { "HID genérico", new GenericHidSource() }, { "XInput", new XInputSource() } };
            try
            {
                foreach (var kv in sources)
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    try
                    {
                        var list = kv.Value.Poll();
                        sb.AppendFormat("{0} ({1:0.0} s): {2}", kv.Key, sw.Elapsed.TotalSeconds, list.Count == 0 ? "nada encontrado" : list.Count + (list.Count == 1 ? " aparelho" : " aparelhos")).AppendLine();
                        foreach (var r in list) sb.AppendLine("   " + Describe(r));
                    }
                    catch (Exception ex) { sb.AppendFormat("{0}: ERRO {1}: {2}", kv.Key, ex.GetType().Name, ex.Message).AppendLine(); }
                }
            }
            finally { HppChannel.DebugLog = prevLog; raw = null; }

            Section(sb, "3. Aparelhos USB/HID conectados");
            try
            {
                var all = Hid.Enumerate();
                foreach (var grp in all.GroupBy(i => i.Vid.ToString("X4") + ":" + i.Pid.ToString("X4") + " " + Hid.Bus(i.Path)).OrderBy(g => g.Key))
                {
                    var first = grp.First();
                    string vendor; Vendors.TryGetValue(first.Vid, out vendor);
                    string product = grp.Select(i => i.Product).FirstOrDefault(p => !string.IsNullOrEmpty(p));
                    string maker = null; try { maker = Hid.Manufacturer(first.Path); } catch { }
                    sb.AppendLine();
                    sb.AppendFormat("{0}  {1} \"{2}\"{3}", grp.Key, vendor ?? "fabricante desconhecido", product ?? "sem nome",
                        string.IsNullOrEmpty(maker) ? "" : " (fabricante informado: " + maker + ")").AppendLine();
                    sb.AppendLine("   tipo provável: " + Kind(DeviceClassifier.Best(DeviceClassifier.FromHid(grp), DeviceClassifier.FromName(product))));
                    bool field = false; int pct = -1;
                    foreach (var c in grp.OrderBy(c => c.Path))
                    {
                        var mi = Regex.Match(c.Path, "&mi_([0-9a-f]{2})", RegexOptions.IgnoreCase);
                        var col = Regex.Match(c.Path, "&col([0-9a-f]{2})", RegexOptions.IgnoreCase);
                        sb.AppendFormat("   coleção {0:X4}/{1:X4} {2,-30} entrada {3,3} B · saída {8,3} B · feature {4,3} B{5}{6}{7}", c.UsagePage, c.Usage, Usage(c), c.InLen, c.FeatLen,
                            mi.Success ? " · interface " + mi.Groups[1].Value : "", col.Success ? " · col " + col.Groups[1].Value : "",
                            c.Vid == 0x1532 && c.FeatLen == 91 ? " · relatório Razer" : "", c.OutLen).AppendLine();
                        if (c.Vid != 0x046D && c.Vid != 0x1532 && pct < 0)
                        {
                            bool f; int v = -1;
                            try { v = Hid.ReadStandardBattery(c, out f); } catch { f = false; }
                            if (f) { field = true; pct = v; }
                        }
                    }
                    if (first.Vid != 0x046D && first.Vid != 0x1532)
                        sb.AppendLine("   campo de bateria padrão HID: " + (!field ? "não tem" : pct >= 0 ? pct + "%" : "tem, mas a leitura falhou"));
                }
            }
            catch (Exception ex) { sb.AppendLine("ERRO ao listar: " + ex.Message); }

            Section(sb, "4. Bluetooth e aparelhos que informam bateria ao Windows");
            try
            {
                var bt = WindowsBatterySource.Diagnose();
                if (bt.Count == 0) sb.AppendLine("(nenhum)");
                foreach (var d in bt)
                    sb.AppendFormat("\"{0}\" · {1} · {2} · bateria: {3}", d.Name ?? "sem nome", d.Bus ?? "?", Kind(d.Kind),
                        d.Battery >= 0 ? d.Battery + "%" : "não informa ao Windows").AppendLine();
            }
            catch (Exception ex) { sb.AppendLine("ERRO: " + ex.Message); }

            Section(sb, "5. Controles XInput");
            try { foreach (var l in XInputSource.Diagnose()) sb.AppendLine(l); }
            catch (Exception ex) { sb.AppendLine("ERRO: " + ex.Message); }

            Section(sb, "6. Programas dos fabricantes abertos");
            for (int i = 0; i < Programs.GetLength(0); i++)
            {
                bool open = Programs[i, 1].Split(',').Any(n => { var ps = System.Diagnostics.Process.GetProcessesByName(n); bool any = ps.Length > 0; foreach (var p in ps) p.Dispose(); return any; });
                sb.AppendLine(Programs[i, 0] + ": " + (open ? "aberto" : "não"));
            }
            try
            {
                string razerLogs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Razer\RazerAppEngine\User Data\Logs");
                var logs = Directory.Exists(razerLogs) ? new DirectoryInfo(razerLogs).GetFiles("lighting-engine*.log") : new FileInfo[0];
                sb.AppendLine("Logs do Razer Synapse 4: " + (logs.Length == 0 ? "não encontrados"
                    : logs.Length + " arquivo(s), o mais recente de " + logs.Max(f => f.LastWriteTime).ToString("dd/MM/yyyy HH:mm")));
            }
            catch (Exception ex) { sb.AppendLine("Logs do Razer Synapse 4: erro " + ex.Message); }

            Section(sb, "7. Detalhes técnicos: respostas de GameSir, MCHOSE e receptores 2.4G");
            sb.Append(rawLog.Length == 0 ? "(nenhum desses aparelhos)" + Environment.NewLine : rawLog.ToString());

            Section(sb, "8. Detalhes técnicos: conversa HID++ com aparelhos Logitech");
            try
            {
                if (File.Exists(hppLog)) { string t = File.ReadAllText(hppLog); sb.Append(t.Length == 0 ? "(nenhuma)" + Environment.NewLine : t); File.Delete(hppLog); }
                else sb.AppendLine("(nenhuma: não há receptor ou aparelho Logitech com HID++)");
            }
            catch (Exception ex) { sb.AppendLine("ERRO: " + ex.Message); }
            return sb.ToString();
        }
    }

    // Item do menu cujo texto muda conforme o andamento (ex.: "Gerando diagnóstico...").
    class StatusItem : FancyItem
    {
        readonly string glyph; readonly Func<string> label; readonly Action action;
        public StatusItem(string glyph, Func<string> label, Action action, float s) : base(30, s)
        { this.glyph = glyph; this.label = label; this.action = action; }

        protected override void Draw(Graphics g, MenuTheme t, float s)
        {
            float cy = Height / 2f;
            Glyph(g, glyph, 14, t.Text, 24 * s, cy);
            using (var f = M.UiFont(12.5f)) DrawLabel(g, label(), f, t.Text, 44 * s, cy - f.Height / 2f - 1 * s, Width - 58 * s);
        }

        protected override void OnClick(EventArgs e) { action(); Invalidate(); base.OnClick(e); } // o menu continua aberto
    }

    // ------------------------------------------------------------------ Aplicativo
    class OverlayApp : ApplicationContext
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string AppName = "BatteryTray";
        static readonly DeviceKind[] Order = { DeviceKind.Headset, DeviceKind.Mouse, DeviceKind.Keyboard, DeviceKind.Controller, DeviceKind.Other };
        readonly IBatterySource[] sources = { new LogitechSource(), new RazerLogSource(), new RazerHidSource(), new WindowsBatterySource(), new GameSirSource(), new BekenMouseSource(), new MchoseHeadsetSource(), new GenericHidSource(), new XInputSource() };
        readonly Dictionary<string, Reading> state = new Dictionary<string, Reading>();
        readonly Dictionary<string, SlotOverlay> overlays = new Dictionary<string, SlotOverlay>();
        readonly Dictionary<string, string> iconSig = new Dictionary<string, string>();
        readonly TrayIcons tray = new TrayIcons();
        readonly SynchronizationContext ui;
        readonly System.Threading.Timer poll;
        readonly System.Windows.Forms.Timer layoutTimer, watchTimer;
        readonly Dictionary<string, Rectangle?> placedAt = new Dictionary<string, Rectangle?>(); // posição usada no último Layout (ícones fixados)

        // aparência (painel ou ícones), o painel na barra e a janela "Personalizar"
        readonly Appearance look = Appearance.Load();
        readonly PanelWindow panel = new PanelWindow();
        AppearanceForm appearanceForm;
        string panelGeom;

        // Se algum ícone fixado mudou de lugar desde o último Layout, refaz na hora (o desenho não fica no lugar antigo).
        // No modo painel, observa a bandeja e a área dos apps abertos.
        void WatchPositions()
        {
            if (look.Panel)
            {
                string g = Native.TaskbarPart("TrayNotifyWnd") + "|" + Native.TaskbarPart("ReBarWindow32") + "|" + Native.TaskbarRect();
                if (g != panelGeom) { panelGeom = g; Layout(); }
                return;
            }
            foreach (var kv in placedAt)
            {
                if (!tray.Has(kv.Key)) continue;
                if (tray.Rect(tray.UidOf(kv.Key)) != kv.Value) { Layout(); return; }
            }
        }
        const string HiddenKey = @"Software\BatteryTray\Hidden";
        readonly HashSet<string> hidden = new HashSet<string>();
        ModernMenu menu;
        int busy;

        // atualizações
        Updater.Release available;
        string updateStatus;
        int updating;
        System.Threading.Timer updateTimer;

        void CheckUpdates(bool manual)
        {
            if (Interlocked.CompareExchange(ref updating, 1, 0) != 0) return;
            if (manual) SetUpdateStatus("Verificando...");
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Updater.Release r = null; string err = null;
                try { r = Updater.Check(); } catch (Exception ex) { err = ex.Message; }
                ui.Post(__ =>
                {
                    Interlocked.Exchange(ref updating, 0);
                    if (err == null) available = r;
                    SetUpdateStatus(!manual ? updateStatus : err != null ? "Falha ao verificar" : r == null ? "Já está atualizado" : null);
                    if (!manual)
                    {
                        // ao ligar o PC a internet pode ainda não estar pronta: tenta de novo em 2 min; senão, daqui a 6 h
                        if (err != null && autoRetries++ < 5) updateTimer.Change(2 * 60 * 1000, 6 * 3600 * 1000);
                        else if (err == null) autoRetries = 0;
                        if (r != null) NotifyUpdate(r);
                    }
                }, null);
            });
        }

        int autoRetries;

        // Notificação de versão nova: uma vez por versão, e de novo a cada 3 dias enquanto não atualizar.
        // Clicar nela abre o menu e já começa a baixar.
        const string NotifiedValue = "AvisoAtualizacao";
        void NotifyUpdate(Updater.Release r)
        {
            string key = Ordered().Select(x => x.Key).FirstOrDefault(x => IsVisible(x) && tray.Has(x)) ?? AppIconKey;
            if (!tray.Has(key)) return; // nenhum ícone na bandeja ainda: avisa na próxima verificação
            string now = r.Tag + "|" + DateTime.Today.ToString("yyyy-MM-dd");
            using (var k = Registry.CurrentUser.CreateSubKey(@"Software\BatteryTray"))
            {
                var last = (k.GetValue(NotifiedValue) as string ?? "").Split('|');
                DateTime when;
                if (last.Length == 2 && last[0] == r.Tag && DateTime.TryParse(last[1], out when) && (DateTime.Today - when).TotalDays < 3) return;
                k.SetValue(NotifiedValue, now);
            }
            pendingClick = "update";
            tray.Notify(key, "BatteryTray " + r.Tag + " disponível", "Clique aqui para atualizar. O app baixa a versão nova e reabre sozinho.");
        }

        string pendingClick; // o que a última notificação faz ao ser clicada

        void NotificationClicked()
        {
            string what = pendingClick; pendingClick = null;
            if (what == "update" && available != null) { ShowMenu(); ApplyUpdate(); }
            else ShowMenu();
        }

        void ApplyUpdate()
        {
            var r = available;
            if (r == null || Interlocked.CompareExchange(ref updating, 1, 0) != 0) return;
            SetUpdateStatus("Baixando " + r.Tag + "...");
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    Updater.Apply(r, true);                // a versão nova abre e espera esta fechar
                    ui.Post(__ => ExitThread(), null);
                }
                catch (Exception ex)
                {
                    ui.Post(__ => { Interlocked.Exchange(ref updating, 0); SetUpdateStatus("Falha ao atualizar"); }, null);
                    try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "BatteryTray-update-error.txt"), ex.ToString()); } catch { }
                }
            });
        }

        void SetUpdateStatus(string s) { updateStatus = s; if (menu != null && menu.Visible) menu.Invalidate(true); }

        // diagnóstico: grava o arquivo na pasta do .exe e abre o Explorer com ele selecionado
        string diagStatus;
        int diagRunning;

        void RunDiagnostics()
        {
            if (Interlocked.CompareExchange(ref diagRunning, 1, 0) != 0) return;
            SetDiagStatus("Gerando diagnóstico...");
            ThreadPool.QueueUserWorkItem(_ =>
            {
                // espera a leitura periódica terminar, para as duas não conversarem com os aparelhos ao mesmo tempo
                while (Interlocked.CompareExchange(ref busy, 1, 0) != 0) Thread.Sleep(100);
                // o que o app mostra, já com o resultado dessa leitura (logo depois de abrir, a primeira ainda pode estar em andamento)
                List<string> shown = null;
                try
                {
                    ui.Send(__ => shown = Ordered().Select(r =>
                    {
                        string li;
                        return (IsVisible(r.Key) ? "[visível] " : "[oculto]  ") + Diagnostics.Describe(r)
                             + (IsVisible(r.Key) && layoutInfo.TryGetValue(r.Key, out li) ? Environment.NewLine + "          barra: " + li : "");
                    }).ToList(), null);
                }
                catch { }
                string path = null;
                try { path = Diagnostics.Write(shown); }
                catch (Exception ex) { try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "BatteryTray-diagnostico-erro.txt"), ex.ToString()); } catch { } }
                finally { Interlocked.Exchange(ref busy, 0); }
                ui.Post(__ =>
                {
                    Interlocked.Exchange(ref diagRunning, 0);
                    if (path == null) { SetDiagStatus("Falha no diagnóstico"); return; }
                    SetDiagStatus(null);
                    if (menu != null) menu.Dismiss();
                    try { System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + path + "\""); } catch { }
                }, null);
            });
        }

        void SetDiagStatus(string s) { diagStatus = s; if (menu != null && menu.Visible) menu.Invalidate(true); }

        public OverlayApp()
        {
            ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
            using (var k = Registry.CurrentUser.OpenSubKey(HiddenKey, true))
                if (k != null)
                    foreach (var n in k.GetValueNames())
                    {
                        string cur = TrayIcons.CurrentKey(n); // aparelho oculto numa versão anterior, com a chave antiga
                        if (cur != n) { k.SetValue(cur, 1, RegistryValueKind.DWord); k.DeleteValue(n, false); }
                        hidden.Add(cur);
                    }
            tray.LeftClick = RefreshNow;
            tray.RightClick = ShowMenu;
            tray.NotificationClick = NotificationClicked;
            tray.Recreated = () => { iconSig.Clear(); Layout(); };
            layoutTimer = new System.Windows.Forms.Timer { Interval = 2000 };
            layoutTimer.Tick += (s, e) => Layout();
            layoutTimer.Start();
            // a bandeja se reorganiza sem avisar (ícone novo, a "^" aparece ou some): confere as posições 4x por segundo
            watchTimer = new System.Windows.Forms.Timer { Interval = 250 };
            watchTimer.Tick += (s, e) => WatchPositions();
            watchTimer.Start();
            SystemEvents.UserPreferenceChanged += (s, e) => { foreach (var o in overlays.Values) o.Invalidate(); panel.Invalidate(); Layout(); };
            panel.LeftClick = RefreshNow;
            panel.RightClick = ShowMenu;
            poll = new System.Threading.Timer(_ => Tick(), null, 0, 60000);
            // verifica atualizações 20 s depois de abrir e a cada 6 horas
            updateTimer = new System.Threading.Timer(_ => ui.Post(__ => CheckUpdates(false), null), null, 5000, 6 * 3600 * 1000);
        }

        public void RefreshNow() { ThreadPool.QueueUserWorkItem(_ => Tick()); }

        // "Atualizar agora" no menu: o menu fica aberto, mostra "Atualizando..." e os valores mudam ali mesmo.
        string refreshStatus;
        void RefreshFromMenu()
        {
            if (refreshStatus != null) return;
            refreshStatus = "Atualizando...";
            if (menu != null && menu.Visible) menu.Invalidate(true);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                while (Volatile.Read(ref busy) != 0) Thread.Sleep(100); // espera a leitura em andamento
                Tick();
                ui.Post(__ => { refreshStatus = null; if (menu != null && menu.Visible) menu.Invalidate(true); }, null); // depois do Apply
            });
        }

        void Tick()
        {
            if (Interlocked.Exchange(ref busy, 1) == 1) return;
            try
            {
                var all = new List<Reading>();
                foreach (var s in sources) { try { all.AddRange(s.Poll()); } catch { } }
                ui.Post(_ => Apply(all), null);
            }
            finally { Interlocked.Exchange(ref busy, 0); }
        }

        IEnumerable<Reading> Ordered() { return state.Values.OrderBy(x => Array.IndexOf(Order, x.Kind)).ThenBy(x => x.Name); }

        void Apply(List<Reading> readings)
        {
            foreach (var r in readings)
            {
                Reading prev;
                if (r.Percent < 0 && state.TryGetValue(r.Key, out prev) && prev.Percent >= 0) { r.Percent = prev.Percent; r.Updated = prev.Updated; }
                if (r.Percent < 0 && !r.Online && !state.ContainsKey(r.Key)) continue; // nunca visto com bateria
                state[r.Key] = r;
            }
            if (menu != null && menu.Visible) menu.Invalidate(true); // atualiza os valores com o menu aberto
            Layout();
            WarnLowBattery();
        }

        // Aviso de bateria baixa: uma notificação ao chegar em 15% e outra em 5%. Volta a avisar depois de carregar.
        const string AlertsValue = "AvisoBateriaBaixa";
        readonly Dictionary<string, int> warnedAt = new Dictionary<string, int>();

        static bool AlertsOn()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(@"Software\BatteryTray")) { var v = k == null ? null : k.GetValue(AlertsValue); return !(v is int) || (int)v != 0; }
        }

        static void SetAlerts(bool on)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(@"Software\BatteryTray")) k.SetValue(AlertsValue, on ? 1 : 0, RegistryValueKind.DWord);
        }

        void WarnLowBattery()
        {
            bool on = AlertsOn();
            foreach (var r in state.Values)
            {
                if (r.Charging || r.Percent > 25) { warnedAt.Remove(r.Key); continue; } // carregou: pode avisar de novo
                if (!on || !r.Online || r.Percent < 0 || !IsVisible(r.Key)) continue;
                int level = r.Percent <= 5 ? 5 : r.Percent <= 15 ? 15 : 0;
                int done;
                if (level == 0 || (warnedAt.TryGetValue(r.Key, out done) && done <= level)) continue;
                warnedAt[r.Key] = level;
                pendingClick = null; // clicar no aviso de bateria só abre o menu
                tray.Notify(NotifyKey(r.Key), "Bateria baixa: " + r.Name,
                    (r.LevelText != null ? "Cerca de " : "") + r.Percent + "% restantes. " + (level == 5 ? "Coloque para carregar agora." : "Coloque para carregar em breve."));
            }
        }

        bool IsVisible(string key) { return !hidden.Contains(key); }

        void ToggleVisible(string key)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(HiddenKey))
            {
                if (hidden.Remove(key)) { if (k.GetValue(key) != null) k.DeleteValue(key); }
                else { hidden.Add(key); k.SetValue(key, 1, RegistryValueKind.DWord); }
            }
            Layout();
        }

        // Para cada aparelho: atualiza o ícone da bandeja e, se ele estiver fixado na barra, desenha o texto em cima dele.
        const string AppIconKey = "__app__";
        IntPtr appIcon;

        // Ícone do BatteryTray (dentro da "^"): no modo painel, sempre (notificações e o menu, se o painel não couber);
        // no modo de ícones, só quando nenhum aparelho está visível.
        string appTip;
        void UpdateAppIcon(bool panelShown)
        {
            bool anyVisible = state.Keys.Any(IsVisible);
            if (!look.Panel && anyVisible) { tray.Remove(AppIconKey); appTip = null; return; }
            string tip = state.Count == 0 ? "BatteryTray: procurando dispositivos..."
                       : !anyVisible ? "BatteryTray: todos os dispositivos estão ocultos"
                       : !panelShown ? "BatteryTray: sem espaço na barra para o painel"
                       : "BatteryTray";
            tip += "\nClique com o botão direito para abrir o menu";
            if (tray.Has(AppIconKey) && tip == appTip) return;
            if (appIcon == IntPtr.Zero) { appIcon = SlotRenderer.AppIcon(); tray.SharedIcons.Add(appIcon); }
            appTip = tip;
            tray.Set(AppIconKey, appIcon, tip);
        }

        // Modo painel: os aparelhos são desenhados num painel ao lado da bandeja, sem um ícone de bandeja para cada um.
        List<PanelRenderer.Item> PanelItems()
        {
            return Ordered().Where(r => IsVisible(r.Key)).OrderBy(r => look.OrderOf(r.Key))
                .Select(r => new PanelRenderer.Item { R = r, Color = look.ColorOf(r), Label = look.LabelOf(r.Key) }).ToList();
        }

        void LayoutPanel()
        {
            foreach (var r in state.Values)
            {
                tray.Remove(r.Key); iconSig.Remove(r.Key);
                SlotOverlay o; if (overlays.TryGetValue(r.Key, out o)) o.Show(r, null);
            }
            placedAt.Clear();
            bool shown = panel.Show(PanelItems(), look);
            UpdateAppIcon(shown);
        }

        // chave do ícone de onde sai a notificação de um aparelho (no modo painel, o ícone do app)
        string NotifyKey(string key) { return tray.Has(key) ? key : AppIconKey; }

        void OpenAppearance()
        {
            if (appearanceForm != null && !appearanceForm.IsDisposed) { appearanceForm.Activate(); return; }
            appearanceForm = new AppearanceForm(look, () => Ordered().ToList(), () => { look.Save(); panel.Invalidate(); Layout(); });
            appearanceForm.Show();
            appearanceForm.Activate();
        }

        readonly Dictionary<string, string> layoutInfo = new Dictionary<string, string>(); // para o diagnóstico

        void Layout()
        {
            if (look.Panel) { LayoutPanel(); return; }
            panel.Hide();
            UpdateAppIcon(false);
            var tb = Native.TaskbarRect();
            var pinned = tray.PinnedUids();
            foreach (var r in Ordered())
            {
                SlotOverlay o;
                if (!overlays.TryGetValue(r.Key, out o))
                {
                    overlays[r.Key] = o = new SlotOverlay();
                    o.LeftClick = RefreshNow;
                }
                if (!IsVisible(r.Key)) // escondido pelo menu: sem ícone e sem texto na barra
                {
                    tray.Remove(r.Key); iconSig.Remove(r.Key); o.Show(r, null);
                    continue;
                }

                int uid = tray.Has(r.Key) ? tray.UidOf(r.Key) : EnsureIcon(r, false);
                Rectangle? rect = tray.Rect(uid);
                bool isPinned = pinned.Contains(uid);
                bool rectOk = rect.HasValue && !tb.IsEmpty
                             && rect.Value.Width > 0 && rect.Value.Top >= tb.Top - 2 && rect.Value.Bottom <= tb.Bottom + 2
                             && rect.Value.Left >= tb.Left && rect.Value.Right <= tb.Right;
                layoutInfo[r.Key] = "ícone " + uid + " · fixado: " + (isPinned ? "sim" : "não") + " · posição: "
                                  + (rect.HasValue ? rect.Value.Left + "," + rect.Value.Top + " " + rect.Value.Width + "x" + rect.Value.Height : "nenhuma")
                                  + (rectOk ? "" : " (fora da barra " + tb.Left + "," + tb.Top + " " + tb.Width + "x" + tb.Height + ")");
                if (isPinned) placedAt[r.Key] = rect; else placedAt.Remove(r.Key);
                // Fixado, mas o Windows não informou a posição agora (tela bloqueada ou em suspensão, jogo em tela cheia,
                // Explorer reorganizando a barra): esconde o desenho até a posição voltar, sem trocar o ícone pelo
                // número simples de "não fixado" e sem deixar o desenho num lugar que pode não ser mais o dele.
                if (isPinned && !rectOk) { o.Show(r, null); continue; }
                bool onBar = isPinned && rectOk;
                EnsureIcon(r, onBar);
                o.Show(r, onBar ? rect : null);
            }
        }

        int EnsureIcon(Reading r, bool pinnedLook)
        {
            string tip = SlotRenderer.Describe(r) + (pinnedLook ? "" : "\nArraste para a barra de tarefas para fixar");
            string sig = pinnedLook + "|" + SlotRenderer.Number(r) + "|" + tip;
            string old;
            if (iconSig.TryGetValue(r.Key, out old) && old == sig && tray.Has(r.Key)) return tray.UidOf(r.Key);
            iconSig[r.Key] = sig;
            return tray.Set(r.Key, pinnedLook ? tray.Blank : SlotRenderer.SmallIcon(r), tip);
        }

        void ShowMenu()
        {
            if (menu != null) { menu.Dismiss(); menu.Dispose(); }
            if (updating == 0) updateStatus = null; // mensagens antigas ("já atualizado") não reaparecem
            if (diagRunning == 0) diagStatus = null;
            menu = BuildMenu();
            Native.SetForegroundWindow(tray.Handle); // faz o menu fechar ao clicar fora
            menu.Show(Cursor.Position, ToolStripDropDownDirection.AboveLeft);
        }

        ModernMenu BuildMenu()
        {
            var m = new ModernMenu();
            float s = m.S;
            if (state.Count > 0)
            {
                m.Items.Add(new HeaderItem("Dispositivos na barra", s));
                foreach (var r in Ordered())
                {
                    string key = r.Key;
                    m.Items.Add(new DeviceItem(() => { Reading x; return state.TryGetValue(key, out x) ? x : null; },
                                               () => IsVisible(key), () => ToggleVisible(key), s));
                }
                m.AddSeparator();
            }
            m.Items.Add(new StatusItem("", () => refreshStatus ?? "Atualizar agora", RefreshFromMenu, s));
            m.Items.Add(new ActionItem("", "Iniciar com o Windows", () => SetAutoStart(!IsAutoStart()), IsAutoStart, s));
            m.Items.Add(new ActionItem("\uEA8F", "Avisar bateria baixa", () => SetAlerts(!AlertsOn()), AlertsOn, s));
            m.Items.Add(new ActionItem("\uE790", "Personalizar...", OpenAppearance, null, s));
            m.Items.Add(new UpdateItem(() => available, () => updateStatus, () => CheckUpdates(true), ApplyUpdate, s));
            m.Items.Add(new StatusItem("", () => diagStatus ?? "Gerar diagnóstico", RunDiagnostics, s));
            m.AddSeparator();
            m.Items.Add(new ActionItem("", "Sair", ExitThread, null, s));
            return m;
        }

        static bool IsAutoStart()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue(AppName) != null;
        }

        public static void SetAutoStart(bool on)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (on) k.SetValue(AppName, "\"" + Application.ExecutablePath + "\"");
                else if (k.GetValue(AppName) != null) k.DeleteValue(AppName);
            }
        }

        protected override void ExitThreadCore()
        {
            poll.Dispose(); layoutTimer.Stop(); watchTimer.Stop(); if (updateTimer != null) updateTimer.Dispose();
            foreach (var o in overlays.Values) o.Dispose();
            panel.Dispose();
            if (appearanceForm != null && !appearanceForm.IsDisposed) appearanceForm.Dispose();
            tray.Dispose();
            base.ExitThreadCore();
        }
    }

    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--dump") { Dump(); return; }
            if (args.Length > 0 && args[0] == "--autostart") { OverlayApp.SetAutoStart(true); return; }
            if (args.Length > 0 && args[0] == "--diagnostico") { Diagnostics.Write(null); return; } // sem abrir a interface
            if (args.Length > 0 && args[0] == "--self-update") { SelfUpdateCli(args.Contains("--no-restart")); return; }
            bool updated = args.Contains("--updated");
            using (var mutex = new Mutex(false, "BatteryTray_SingleInstance"))
            {
                bool owned;
                // depois de uma atualização, espera a versão antiga terminar de fechar
                try { owned = mutex.WaitOne(updated ? 15000 : 0); } catch (AbandonedMutexException) { owned = true; }
                if (!owned) return;
                try
                {
                    Updater.CleanupOld();
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    Application.Run(new OverlayApp());
                }
                finally { mutex.ReleaseMutex(); }
            }
        }

        // Linha de comando (teste): verifica e aplica a atualização, gravando o resultado em update.txt.
        static void SelfUpdateCli(bool noRestart)
        {
            string log = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "update.txt");
            try
            {
                var r = Updater.Check();
                if (r == null) { File.WriteAllText(log, "atual: v" + Updater.Version + " (sem atualização)"); return; }
                Updater.Apply(r, !noRestart);
                File.WriteAllText(log, "atualizado de v" + Updater.Version + " para " + r.Tag);
            }
            catch (Exception ex) { File.WriteAllText(log, "erro: " + ex.Message); }
        }

        // Modo de teste: imprime as leituras e salva uma prévia (ampliada 4x) de como cada aparelho aparece na barra.
        static void Dump()
        {
            var outDir = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "preview");
            Directory.CreateDirectory(outDir);
            HppChannel.DebugLog = Path.Combine(outDir, "hpp.log");
            File.WriteAllText(HppChannel.DebugLog, "");
            var all = new List<Reading>();
            foreach (var s in new IBatterySource[] { new LogitechSource(), new RazerLogSource(), new RazerHidSource(), new WindowsBatterySource(), new GameSirSource(), new BekenMouseSource(), new MchoseHeadsetSource(), new GenericHidSource(), new XInputSource() }) all.AddRange(s.Poll());
            File.WriteAllLines(Path.Combine(outDir, "dump.txt"), all.Select(r => string.Format("{0} | {1} | {2}% | online={3} | charging={4} | {5}", r.Key, r.Name, r.Percent, r.Online, r.Charging, r.Updated)));
            all.Add(new Reading { Kind = DeviceKind.Mouse, Percent = 100, Online = true, Charging = true });
            all.Add(new Reading { Kind = DeviceKind.Headset, Percent = 45, Online = true, Charging = true });
            all.Add(new Reading { Kind = DeviceKind.Controller, Percent = 9, Online = true });
            all.Add(new Reading { Kind = DeviceKind.Keyboard, Percent = 100, Online = true });
            // prévia do painel (escuro e claro), com os exemplos da imagem de referência e os estados especiais
            var look = new Appearance();
            Func<DeviceKind, int, bool, bool, string, PanelRenderer.Item> mk = (k, p, on, ch, lbl) =>
                new PanelRenderer.Item { R = new Reading { Key = "x" + k + p, Kind = k, Percent = p, Online = on, Charging = ch, Name = k.ToString() }, Color = Appearance.DefaultColor(k), Label = lbl };
            var sample = new List<PanelRenderer.Item> {
                mk(DeviceKind.Headset, 90, true, false, "600"), mk(DeviceKind.Headset, 100, true, false, "650"), mk(DeviceKind.Mouse, 75, true, false, "G502"), mk(DeviceKind.Headset, 80, false, false, "V3Pro"),
                mk(DeviceKind.Mouse, 100, true, false, ""), mk(DeviceKind.Controller, 85, true, false, ""),
                mk(DeviceKind.Mouse, 45, true, true, ""), mk(DeviceKind.Keyboard, 9, true, false, ""), mk(DeviceKind.Headset, 80, false, false, "") };
            int pw = 0;
            foreach (int size in new[] { 1, 0, 2 }) { look.Size = size; pw = Math.Max(pw, PanelRenderer.Layout(sample, look, 1f)); }
            using (var panelSheet = new Bitmap(pw + 20, 48 * 6 + 10))
            using (var g = Graphics.FromImage(panelSheet))
            {
                g.Clear(Color.Gray);
                int row = 0;
                foreach (bool light in new[] { false, true })
                    foreach (int size in new[] { 1, 0, 2 })
                    {
                        look.Size = size;
                        int ww = PanelRenderer.Layout(sample, look, 1f);
                        using (var bg = new SolidBrush(light ? Color.FromArgb(238, 238, 238) : Color.FromArgb(28, 28, 28))) g.FillRectangle(bg, 0, row * 48, panelSheet.Width, 48);
                        using (var b = PanelRenderer.Render(sample, look, 1f, ww, 48, light, row == 0 ? 0 : -1)) g.DrawImage(b, 10, row * 48);
                        row++;
                    }
                panelSheet.Save(Path.Combine(outDir, "panel.png"), ImageFormat.Png);
            }

            int w = 32, h = 48, gap = 8, z = 4;
            using (var sheet = new Bitmap((w + gap) * all.Count * z, h * z))
            using (var g = Graphics.FromImage(sheet))
            {
                g.Clear(Color.FromArgb(28, 28, 28));
                g.InterpolationMode = InterpolationMode.NearestNeighbor; g.PixelOffsetMode = PixelOffsetMode.Half;
                for (int i = 0; i < all.Count; i++)
                    using (var b = SlotRenderer.Render(all[i], w, h, 1f))
                    {
                        g.DrawImage(b, i * (w + gap) * z, 0, w * z, h * z);
                        g.DrawRectangle(Pens.DimGray, i * (w + gap) * z, 0, w * z - 1, h * z - 1);
                    }
                sheet.Save(Path.Combine(outDir, "slots.png"), ImageFormat.Png);
            }
        }
    }
}
