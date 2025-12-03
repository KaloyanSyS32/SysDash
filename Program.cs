#pragma warning disable CA1416
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using System.Runtime.Versioning;

[SupportedOSPlatform("windows")]
public class SystemInfo
{
    public string OS { get; }
    public string Version { get; }
    public string Model { get; }
    public string CPU { get; }
    public string GPU { get; }
    public double RAM { get; }

    public SystemInfo()
    {
        var ver = Environment.OSVersion.Version;
        string name = ver.Build >= 22000 ? "Windows 11" : "Windows 10";
        OS = name;
        Version = $"{ver.Major}.{ver.Minor}.{ver.Build}";
        Model = Wmi("Win32_ComputerSystem", "Model");
        CPU = Wmi("Win32_Processor", "Name");
        GPU = Wmi("Win32_VideoController", "Name");
        RAM = TotalRam();
    }

    private static string Wmi(string cls, string prop)
    {
        try
        {
            using var s = new ManagementObjectSearcher($"select {prop} from {cls}");
            foreach (var o in s.Get()) return o[prop]?.ToString() ?? "Unknown";
        }
        catch { }
        return "Unknown";
    }

    private static double TotalRam()
    {
        try
        {
            double total = 0;
            using var s = new ManagementObjectSearcher("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem");
            foreach (var o in s.Get())
                total += Convert.ToDouble(o["TotalPhysicalMemory"]);
            return Math.Round(total / Math.Pow(1024, 3), 2);
        }
        catch { return 0; }
    }
}

[SupportedOSPlatform("windows")]
public class StatsMonitor
{
    private readonly PerformanceCounter cpu;
    private readonly PerformanceCounter ram;
    private readonly PerformanceCounter disk;

    public StatsMonitor()
    {
        cpu = new PerformanceCounter("Processor", "% Processor Time", "_Total");
        ram = new PerformanceCounter("Memory", "% Committed Bytes In Use");
        disk = new PerformanceCounter("LogicalDisk", "% Free Space", "C:");
        _ = cpu.NextValue();
    }

    public object Snapshot()
    {
        double cpuLoad = Cpu();
        double ramLoad = ram.NextValue();
        double diskUse = 100 - disk.NextValue();
        string up = Uptime();
        return new { cpu = cpuLoad, ram = ramLoad, disk = diskUse, uptime = up };
    }

    private double Cpu() { Thread.Sleep(200); return cpu.NextValue(); }

    private static string Uptime()
    {
        var t = TimeSpan.FromMilliseconds(Environment.TickCount64);
        return $"{t.Days}d {t.Hours}h {t.Minutes}m {t.Seconds}s";
    }
}

public class Program
{
    [STAThread]
    public static async Task Main(string[] args)
    {
        try { await RunAsync(args); }
        catch (Exception ex)
        {
            File.WriteAllText("error.log", ex.ToString());
            MessageBox.Show("Error logged to error.log", "SysDash Crash", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static async Task RunAsync(string[] args)
    {
        var sys = new SystemInfo();
        var stats = new StatsMonitor();

        Application.EnableVisualStyles();
        NotifyIcon tray = new();

        // Load embedded icon resource
        try
        {
            using var stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("SysDash.icon.ico");
            tray.Icon = stream != null ? new Icon(stream) : SystemIcons.Application;
        }
        catch
        {
            tray.Icon = SystemIcons.Application;
        }

        tray.Visible = true;
        tray.Text = "SysDash is running on localhost:5000";

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open Dashboard", null, (s, e) =>
            Process.Start(new ProcessStartInfo("http://localhost:5000") { UseShellExecute = true }));
        menu.Items.Add("Exit", null, (s, e) => Environment.Exit(0));
        tray.ContextMenuStrip = menu;

        tray.BalloonTipTitle = "SysDash Running";
        tray.BalloonTipText = "Monitoring active → http://localhost:5000";
        tray.ShowBalloonTip(3000);

        var builder = WebApplication.CreateBuilder(args);
        var app = builder.Build();

        app.MapGet("/", async ctx =>
        {
            string html = $@"
<!DOCTYPE html><html lang='en'><head><meta charset='UTF-8'>
<title>SysDash Dashboard</title>
<style>
body{{background:#0f0f0f;color:#e0e0e0;font-family:'JetBrains Mono',monospace;text-align:center;padding:40px;}}
.grid{{display:grid;grid-template-columns:repeat(auto-fit,minmax(220px,1fr));gap:20px;max-width:900px;margin:0 auto;}}
.card{{background:#181818;border-radius:14px;padding:20px;box-shadow:0 0 12px #0008;}}
h1{{color:#9dfc91;}}
</style></head><body>
<h1>SysDash — Local System Monitor</h1>
<div class='grid'>
  <div class='card'><h3>OS</h3><p>{sys.OS}</p></div>
  <div class='card'><h3>Version</h3><p>{sys.Version}</p></div>
  <div class='card'><h3>Model</h3><p>{sys.Model}</p></div>
  <div class='card'><h3>CPU</h3><p>{sys.CPU}</p></div>
  <div class='card'><h3>GPU</h3><p>{sys.GPU}</p></div>
  <div class='card'><h3>RAM</h3><p>{sys.RAM} GB</p></div>
  <div class='card'><h3>CPU Load</h3><p id='cpu'>--%</p></div>
  <div class='card'><h3>RAM Usage</h3><p id='ram'>--%</p></div>
  <div class='card'><h3>Disk Usage</h3><p id='disk'>--%</p></div>
  <div class='card'><h3>Uptime</h3><p id='uptime'>--</p></div>
</div>
<script>
async function update(){{const r=await fetch('/api/stats');const d=await r.json();
cpu.textContent=d.cpu.toFixed(1)+'%';ram.textContent=d.ram.toFixed(1)+'%';
disk.textContent=d.disk.toFixed(1)+'%';uptime.textContent=d.uptime;}}
setInterval(update,800);update();
</script></body></html>";
            await ctx.Response.WriteAsync(html);
        });

        app.MapGet("/api/stats", () => Results.Json(stats.Snapshot()));

        _ = Task.Run(() => app.Run("http://0.0.0.0:5000"));
        Application.Run();
    }
}
