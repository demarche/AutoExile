using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace AutoExile.Systems;

/// <summary>
/// Direct Carry ⇔ Aurabot channel (2026-09-25, user: "TCPなどの通信経路を確立し…").
/// Both ExileAPI instances run on the same PC (the Aurabot in another Windows session), so the loopback interface is
/// shared: each side binds its own UDP port on 127.0.0.1 and sends small JSON datagrams to the other at ~10 Hz.
/// UDP instead of TCP: no connection state to recover after a restart of either side, and a lost packet is simply
/// replaced by the next one 100 ms later. A peer is "alive" while its last datagram is younger than 2 s.
/// </summary>
public sealed class DuoLink : IDisposable
{
    public const int CarryPort = 9890;   // the Carry (AwakeningBossRush) listens here
    public const int AuraPort = 9891;    // the Aurabot (Follower) listens here

    private UdpClient? _udp;
    private IPEndPoint? _peer;
    private int _listenPort;
    private readonly ConcurrentQueue<string> _inbox = new();
    private CancellationTokenSource? _cts;
    private long _seq;
    public string Role { get; private set; } = "";
    public string LastError { get; private set; } = "";
    public DateTime LastReceivedUtc { get; private set; } = DateTime.MinValue;
    public DuoPacket? Last { get; private set; }
    public long Sent { get; private set; }
    public long Received { get; private set; }
    public bool Alive => Last != null && (DateTime.UtcNow - LastReceivedUtc).TotalSeconds < 2.0;
    public double AgeMs => Last == null ? double.PositiveInfinity : (DateTime.UtcNow - LastReceivedUtc).TotalMilliseconds;

    /// <summary>Start (or keep) the socket for this role: "carry" or "aura".</summary>
    public void Ensure(string role)
    {
        if (_udp != null && Role == role) return;
        Stop();
        Role = role;
        _listenPort = role == "carry" ? CarryPort : AuraPort;
        _peer = new IPEndPoint(IPAddress.Loopback, role == "carry" ? AuraPort : CarryPort);
        try
        {
            _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, _listenPort));
            // Windows reports an ICMP "port unreachable" (peer not running yet) as WSAECONNRESET on the next receive;
            // SIO_UDP_CONNRESET = false turns that off so a missing peer is just silence.
            try { _udp.Client.IOControl(unchecked((int)0x9800000C), new byte[] { 0 }, null); } catch { }
            _cts = new CancellationTokenSource();
            var token = _cts.Token; var udp = _udp;
            _ = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        var r = await udp.ReceiveAsync(token);
                        _inbox.Enqueue(Encoding.UTF8.GetString(r.Buffer));
                        while (_inbox.Count > 64) _inbox.TryDequeue(out _);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (ObjectDisposedException) { break; }
                    catch (Exception ex) { LastError = ex.Message; await Task.Delay(200); }
                }
            }, token);
            LastError = "";
        }
        catch (Exception ex) { LastError = $"bind {_listenPort}: {ex.Message}"; _udp = null; }
    }

    /// <summary>Drain received datagrams on the game thread; keeps the newest valid packet.</summary>
    public void Poll()
    {
        while (_inbox.TryDequeue(out var json))
        {
            try
            {
                var p = JsonSerializer.Deserialize<DuoPacket>(json, Opts);
                if (p == null) continue;
                if (Last != null && p.Seq <= Last.Seq && p.Boot == Last.Boot) continue; // stale / reordered
                Last = p; LastReceivedUtc = DateTime.UtcNow; Received++;
            }
            catch (Exception ex) { LastError = "parse: " + ex.Message; }
        }
    }

    private static readonly string BootId = Guid.NewGuid().ToString("N")[..8];
    public void Send(DuoPacket p)
    {
        if (_udp == null || _peer == null) return;
        p.Seq = ++_seq; p.Boot = BootId; p.Role = Role; p.Utc = DateTime.UtcNow.Ticks;
        try
        {
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(p, Opts));
            _udp.Send(bytes, bytes.Length, _peer);
            Sent++;
        }
        catch (Exception ex) { LastError = "send: " + ex.Message; }
    }

    public object Snapshot() => new
    {
        Role, alive = Alive, ageMs = double.IsInfinity(AgeMs) ? -1 : Math.Round(AgeMs), Sent, Received, LastError,
        peer = Last == null ? null : new { Last.Name, Last.Area, Last.AreaHash, Last.X, Last.Y, Last.Cmd, Last.Phase, Last.LinkOk, Last.HpPct, Last.EsPct }
    };

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _udp?.Dispose(); } catch { }
        _udp = null; _cts = null;
    }
    public void Dispose() => Stop();

    private static readonly JsonSerializerOptions Opts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
}

/// <summary>
/// One datagram. The Carry fills its intent (where it is going next, where the Aurabot should wait, commands); the
/// Aurabot answers with its own position, health and Soul Link bookkeeping.
/// Commands (Carry → Aurabot): "follow" (default), "come" (close in now, ignore everything else), "hold" (wait at
/// HoldX/HoldY), "hold_map" (Carry died / is re-entering: stay in the map, do not take a portal), "portal" (Carry is
/// entering the map portal at PortalX/PortalY — follow right away), "link" (recast Soul Link now), "home" (map done:
/// go back to the Carry's hideout).
/// </summary>
public sealed class DuoPacket
{
    public long Seq { get; set; }
    public string Boot { get; set; } = "";
    public string Role { get; set; } = "";
    public long Utc { get; set; }
    public string Name { get; set; } = "";
    public string Area { get; set; } = "";
    public long AreaHash { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Vx { get; set; }
    public float Vy { get; set; }
    public bool HasDest { get; set; }
    public float DestX { get; set; }
    public float DestY { get; set; }
    public bool HasHold { get; set; }
    public float HoldX { get; set; }
    public float HoldY { get; set; }
    public bool HasPortal { get; set; }
    public float PortalX { get; set; }
    public float PortalY { get; set; }
    public string Cmd { get; set; } = "follow";
    public string Phase { get; set; } = "";
    public bool InMap { get; set; }
    public bool Fighting { get; set; }
    /// <summary>Carry: Soul Link buff present on the Carry. Aurabot: link cast recently and target in range.</summary>
    public bool LinkOk { get; set; }
    public float LinkLeftSec { get; set; }
    public int HpPct { get; set; }
    public int EsPct { get; set; }
    public float AuraRadius { get; set; }
    public float PartnerDist { get; set; }
    /// <summary>Aurabot: its own aura buffs ("player_aura_*", read from its Buffs component).</summary>
    public string[]? Auras { get; set; }
    /// <summary>Carry: whether one of the Aurabot's own auras is currently applied to it (memory, not distance).</summary>
    public bool AuraKnown { get; set; }
    public bool InAura { get; set; }
    /// <summary>Carry: increments on every blink/dash; BlinkX/Y = where it is going (Aurabot blinks after it).</summary>
    public long BlinkSeq { get; set; }
    public float BlinkX { get; set; }
    public float BlinkY { get; set; }
    /// <summary>Carry: map portals still open in the hideout (the last one is reserved for the Carry).</summary>
    public int PortalsLeft { get; set; }
    /// <summary>Carry: area hash of the map its current run owns (0 = none). The Aurabot only holds / waits in that map.</summary>
    public long MapHash { get; set; }

    public Vector2 Pos => new(X, Y);
    public Vector2 Dest => new(DestX, DestY);
    public Vector2 Hold => new(HoldX, HoldY);
    public Vector2 Portal => new(PortalX, PortalY);
}
