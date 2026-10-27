using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace ResetExplode;

/// <summary>Descoberta local por ping/ARP e Wake-on-LAN. Não tenta acessar ou configurar dispositivos.</summary>
public sealed class LanDevice
{
    public string Name { get; init; } = "Dispositivo sem nome";
    public string Ip { get; init; } = "—";
    public string Mac { get; init; } = "—";
    public string Kind { get; init; } = "Dispositivo local";
    public string Connection { get; init; } = "Desconhecido";
    public bool IsRouter { get; init; }
    public bool CanWake => Regex.IsMatch(Mac, "^(?:[0-9A-F]{2}[:-]){5}[0-9A-F]{2}$", RegexOptions.IgnoreCase);
}

public sealed record LanScanResult(List<LanDevice> Devices, string Gateway, string Adapter, string LocalIp);

public static class LanDevices
{
    private static NetworkInterface? ActiveAdapter() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up
                    && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    && n.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)))
        .OrderByDescending(n => n.Speed).FirstOrDefault();

    public static string? DefaultGateway()
    {
        try
        {
            return ActiveAdapter()?.GetIPProperties().GatewayAddresses
                .FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any))?.Address.ToString();
        }
        catch { return null; }
    }

    public static async Task<LanScanResult> ScanAsync(Action<string> progress, CancellationToken ct)
    {
        var nic = ActiveAdapter() ?? throw new InvalidOperationException("Nenhuma conexão local ativa foi encontrada.");
        var props = nic.GetIPProperties();
        var local = props.UnicastAddresses.FirstOrDefault(u => u.Address.AddressFamily == AddressFamily.InterNetwork)?.Address
                    ?? throw new InvalidOperationException("O adaptador não tem IPv4.");
        var mask = props.UnicastAddresses.FirstOrDefault(u => u.Address.Equals(local))?.IPv4Mask ?? IPAddress.Parse("255.255.255.0");
        var gateway = props.GatewayAddresses.First(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)).Address;
        var (first, last) = Hosts(local, mask);
        var ips = Enumerable.Range(first, last - first + 1).Select(ToIp).Where(ip => !ip.Equals(local)).ToList();
        progress($"Procurando dispositivos em {local}/{Prefix(mask)}…");

        var alive = new List<IPAddress> { gateway };
        using var limit = new SemaphoreSlim(40);
        await Task.WhenAll(ips.Select(async ip =>
        {
            await limit.WaitAsync(ct);
            try
            {
                using var ping = new Ping();
                var reply = await ping.SendPingAsync(ip, 350);
                if (reply.Status == IPStatus.Success) lock (alive) alive.Add(ip);
            }
            catch { }
            finally { limit.Release(); }
        }));
        ct.ThrowIfCancellationRequested();

        progress("Lendo endereços físicos e nomes…");
        var arp = ReadArp();
        var devices = new List<LanDevice>();
        foreach (var ip in alive.Distinct().OrderBy(x => ToUInt(x)))
        {
            ct.ThrowIfCancellationRequested();
            string ipText = ip.ToString();
            arp.TryGetValue(ipText, out var mac);
            bool router = ip.Equals(gateway);
            string name = router ? "Roteador / gateway" : await HostName(ip, ct) ?? "Dispositivo local";
            devices.Add(new LanDevice
            {
                Name = name, Ip = ipText, Mac = mac ?? "—", IsRouter = router,
                Kind = router ? "Roteador" : "Dispositivo ativo", Connection = router ? "Gateway" : "Cabo ou Wi-Fi não informado pelo roteador"
            });
        }
        return new LanScanResult(devices, gateway.ToString(), nic.Name, local.ToString());
    }

    public static async Task SendWakeOnLanAsync(string mac, string? gateway = null)
    {
        var bytes = mac.Split('-', ':').Select(x => Convert.ToByte(x, 16)).ToArray();
        if (bytes.Length != 6) throw new ArgumentException("MAC inválido.");
        var packet = new byte[102];
        Array.Fill(packet, (byte)0xFF, 0, 6);
        for (int i = 6; i < packet.Length; i += 6) Array.Copy(bytes, 0, packet, i, 6);
        using var udp = new UdpClient { EnableBroadcast = true };
        await udp.SendAsync(packet, packet.Length, new IPEndPoint(IPAddress.Broadcast, 9));
        if (IPAddress.TryParse(gateway, out var gw))
            await udp.SendAsync(packet, packet.Length, new IPEndPoint(gw, 9));
    }

    private static async Task<string?> HostName(IPAddress ip, CancellationToken ct)
    {
        try
        {
            var lookup = Dns.GetHostEntryAsync(ip);
            var done = await Task.WhenAny(lookup, Task.Delay(450, ct));
            if (done != lookup) return null;
            var name = (await lookup).HostName;
            return string.IsNullOrWhiteSpace(name) ? null : name.Split('.')[0];
        }
        catch { return null; }
    }

    private static Dictionary<string, string> ReadArp()
    {
        var map = new Dictionary<string, string>();
        try
        {
            using var p = Process.Start(new ProcessStartInfo("arp", "-a") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true });
            string text = p?.StandardOutput.ReadToEnd() ?? ""; p?.WaitForExit();
            foreach (Match m in Regex.Matches(text, @"(?<ip>\d{1,3}(?:\.\d{1,3}){3})\s+(?<mac>(?:[0-9A-Fa-f]{2}-){5}[0-9A-Fa-f]{2})"))
                map[m.Groups["ip"].Value] = m.Groups["mac"].Value.ToUpperInvariant();
        }
        catch { }
        return map;
    }

    private static (int First, int Last) Hosts(IPAddress ip, IPAddress mask)
    {
        uint network = ToUInt(ip) & ToUInt(mask); uint broadcast = network | ~ToUInt(mask);
        int first = (int)Math.Max(network + 1, ToUInt(ip) - 254); int last = (int)Math.Min(broadcast - 1, first + 253);
        return (first, last);
    }
    private static uint ToUInt(IPAddress ip) => BitConverter.ToUInt32(ip.GetAddressBytes().Reverse().ToArray());
    private static IPAddress ToIp(int value) => new(BitConverter.GetBytes((uint)value).Reverse().ToArray());
    private static int Prefix(IPAddress mask) => mask.GetAddressBytes().Sum(b => Convert.ToString(b, 2).Count(c => c == '1'));
}
