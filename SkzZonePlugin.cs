using System;
using System.Collections.Generic;
using System.IO;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using System.Text.Json;
// "Timer" adı System.Threading.Timer ile çakışıyordu (CS0104).
// Bu alias sayesinde dosyadaki her "Timer" artık kesin olarak CounterStrikeSharp'ınki.
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace SkzZone;

// Cross ve Zone verisinin JSON olarak diske yazılan hali.
// Bu sayede server kapanıp açılsa bile noktalar silinmez.
public class SkzData
{
    public float[]? CrossPos { get; set; }   // Herkesin SKZ başında çekileceği nokta
    public float CrossAngle { get; set; }    // Cross noktasındaki bakış açısı (yaw)
    public float[]? ZoneMin { get; set; }    // Güvenli bölge kutusunun min köşesi
    public float[]? ZoneMax { get; set; }    // Güvenli bölge kutusunun max köşesi
}

[MinimumApiVersion(80)]
public class SkzZonePlugin : BasePlugin
{
    public override string ModuleName => "SKZ Zone (Jailbreak)";
    public override string ModuleVersion => "1.0.0";
    public override string ModuleAuthor => "Custom";
    public override string ModuleDescription => "!zone, !cross ve !skz <saniye> komutlarıyla Jailbreak SKZ minik oyunu";

    private string ConfigPath => Path.Combine(ModuleDirectory, "skz_data.json");
    private SkzData _data = new();

    // !zone komutu iki adımlı çalışır: ilk köşe burada bekletilir.
    private Vector? _zoneCorner1;

    // Aktif SKZ turunun durumu
    private bool _roundActive;
    private readonly HashSet<int> _safePlayers = new();
    private Timer? _checkTimer;
    private Timer? _countdownTimer;

    public override void Load(bool hotReload)
    {
        LoadData();
        Logger.LogInformation("SKZ Zone plugin yüklendi. Cross ayarlı: {0}, Zone ayarlı: {1}",
            _data.CrossPos != null, _data.ZoneMin != null);
    }

    private void LoadData()
    {
        if (!File.Exists(ConfigPath)) return;
        try
        {
            var json = File.ReadAllText(ConfigPath);
            _data = JsonSerializer.Deserialize<SkzData>(json) ?? new SkzData();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "skz_data.json okunamadı, sıfırdan başlanıyor.");
            _data = new SkzData();
        }
    }

    private void SaveData()
    {
        var json = JsonSerializer.Serialize(_data, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(ConfigPath, json);
    }

    // ---------- !cross : SKZ başlangıç noktasını (haç) kaydeder ----------
    [RequiresPermissions("@css/root")]
    [CommandHelper(minArgs: 0, usage: "", whoCanExecute: CommandUsage.CLIENT_ONLY)]
    [ConsoleCommand("css_cross", "SKZ başlangıç (cross) noktasını bulunduğun yere kaydeder")]
    public void OnCrossCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null || !player.IsValid) return;
        var pawn = player.PlayerPawn.Value;
        if (pawn == null) return;

        var pos = pawn.AbsOrigin;
        var ang = pawn.AbsRotation;
        if (pos == null) return;

        _data.CrossPos = new[] { pos.X, pos.Y, pos.Z };
        _data.CrossAngle = ang?.Y ?? 0f;
        SaveData();

        player.PrintToChat(" \x04[SKZ]\x01 Cross noktası kaydedildi ve diske yazıldı.");
    }

    // ---------- !zone : güvenli bölgeyi iki köşeden kaydeder ----------
    [RequiresPermissions("@css/root")]
    [CommandHelper(minArgs: 0, usage: "", whoCanExecute: CommandUsage.CLIENT_ONLY)]
    [ConsoleCommand("css_zone", "Zone kurulumu: bir köşeye gidip yaz, sonra karşı köşeye gidip tekrar yaz")]
    public void OnZoneCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null || !player.IsValid) return;
        var pawn = player.PlayerPawn.Value;
        if (pawn == null) return;

        var pos = pawn.AbsOrigin;
        if (pos == null) return;

        if (_zoneCorner1 == null)
        {
            _zoneCorner1 = new Vector(pos.X, pos.Y, pos.Z);
            player.PrintToChat(" \x04[SKZ]\x01 1. köşe kaydedildi. Şimdi karşı köşeye gidip tekrar !zone yaz.");
            return;
        }

        var c1 = _zoneCorner1;
        var c2 = new Vector(pos.X, pos.Y, pos.Z);

        // Yükseklik payı: oyuncunun ayağından biraz aşağı, başından biraz yukarı
        _data.ZoneMin = new[] { Math.Min(c1.X, c2.X), Math.Min(c1.Y, c2.Y), Math.Min(c1.Z, c2.Z) - 40f };
        _data.ZoneMax = new[] { Math.Max(c1.X, c2.X), Math.Max(c1.Y, c2.Y), Math.Max(c1.Z, c2.Z) + 90f };
        SaveData();
        _zoneCorner1 = null;

        player.PrintToChat(" \x04[SKZ]\x01 Zone kaydedildi ve diske yazıldı. Server restart olsa da silinmez.");
    }

    // ---------- !skz <saniye> : turu başlatır ----------
    [RequiresPermissions("@css/root")]
    [CommandHelper(minArgs: 1, usage: "<saniye>", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    [ConsoleCommand("css_skz", "SKZ turunu başlatır. Örnek: !skz 5")]
    public void OnSkzCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (_data.CrossPos == null || _data.ZoneMin == null || _data.ZoneMax == null)
        {
            info.ReplyToCommand(" \x02[SKZ]\x01 Önce !cross ve !zone ile noktaları kaydetmen lazım.");
            return;
        }

        if (!int.TryParse(info.GetArg(1), out var seconds) || seconds <= 0)
        {
            info.ReplyToCommand(" \x02[SKZ]\x01 Kullanım: !skz <saniye>  (örnek: !skz 5)");
            return;
        }

        if (_roundActive)
        {
            info.ReplyToCommand(" \x02[SKZ]\x01 Zaten aktif bir SKZ turu var.");
            return;
        }

        StartSkzRound(seconds);
    }

    private void StartSkzRound(int seconds)
    {
        _roundActive = true;
        _safePlayers.Clear();

        var c = _data.CrossPos!;
        var crossVec = new Vector(c[0], c[1], c[2]);
        var crossAngle = new QAngle(0, _data.CrossAngle, 0);

        var pulled = 0;
        foreach (var p in Utilities.GetPlayers())
        {
            if (p is not { IsValid: true, PawnIsAlive: true }) continue;
            var pawn = p.PlayerPawn.Value;
            if (pawn == null) continue;
            if (p.TeamNum != (byte)CsTeam.Terrorist) continue; // sadece tutsaklar (T) çekilir

            pawn.Teleport(crossVec, crossAngle, new Vector(0, 0, 0));
            pulled++;
        }

        BroadcastToAll($" \x04[SKZ]\x01 SKZ başladı! {pulled} kişi çekildi. {seconds} saniye içinde zone'a gir, giremeyen ölür!");

        _checkTimer = AddTimer(0.25f, CheckZoneEntries, TimerFlags.REPEAT);
        _countdownTimer = AddTimer(seconds, EndSkzRound);
    }

    private void CheckZoneEntries()
    {
        if (!_roundActive) return;

        var min = _data.ZoneMin!;
        var max = _data.ZoneMax!;

        foreach (var p in Utilities.GetPlayers())
        {
            if (p is not { IsValid: true, PawnIsAlive: true }) continue;
            var pawn = p.PlayerPawn.Value;
            if (pawn == null) continue;
            if (p.TeamNum != (byte)CsTeam.Terrorist) continue;
            if (_safePlayers.Contains(p.Slot)) continue;

            var pos = pawn.AbsOrigin;
            if (pos == null) continue;

            if (pos.X >= min[0] && pos.X <= max[0] &&
                pos.Y >= min[1] && pos.Y <= max[1] &&
                pos.Z >= min[2] && pos.Z <= max[2])
            {
                _safePlayers.Add(p.Slot);
                p.PrintToChat(" \x04[SKZ]\x01 Zone'a girdin, güvendesin!");
            }
        }
    }

    private void EndSkzRound()
    {
        _roundActive = false;
        _checkTimer?.Kill();
        _checkTimer = null;

        var killed = 0;
        var survived = 0;

        foreach (var p in Utilities.GetPlayers())
        {
            if (p is not { IsValid: true, PawnIsAlive: true }) continue;
            var pawn = p.PlayerPawn.Value;
            if (pawn == null) continue;
            if (p.TeamNum != (byte)CsTeam.Terrorist) continue;

            if (_safePlayers.Contains(p.Slot))
            {
                survived++;
            }
            else
            {
                pawn.CommitSuicide(false, true);
                killed++;
            }
        }

        BroadcastToAll($" \x04[SKZ]\x01 Tur bitti! Kurtulan: {survived} | Ölen: {killed}");
        _safePlayers.Clear();
    }

    // Server.PrintToChatAll'ın CS2Sharp sürümden sürüme değişebilen imzasına bağımlı kalmamak için
    // tüm bağlı oyunculara tek tek yazıyoruz - her sürümde çalışacağı doğrulanmış yöntem bu.
    private static void BroadcastToAll(string message)
    {
        foreach (var p in Utilities.GetPlayers())
        {
            if (p is { IsValid: true })
                p.PrintToChat(message);
        }
    }
}
