using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
namespace DSRMeteorPilot;

public sealed partial class Plugin : IDalamudPlugin
{
    [PluginService] private static IDalamudPluginInterface Pi { get; set; } = null!;
    [PluginService] private static ICommandManager Commands { get; set; } = null!;
    [PluginService] private static IChatGui Chat { get; set; } = null!;
    [PluginService] private static IClientState Client { get; set; } = null!;
    [PluginService] private static IObjectTable Objects { get; set; } = null!;
    [PluginService] private static IFramework Framework { get; set; } = null!;
    [PluginService] private static ICondition Conditions { get; set; } = null!;
    [PluginService] private static IKeyState Keys { get; set; } = null!;
    [PluginService] private static IGameInteropProvider Interop { get; set; } = null!;
    [PluginService] private static IPluginLog Log { get; set; } = null!;
    private readonly Configuration config;
    private readonly Encounter encounter = new();
    private readonly EventSource? events;
    private readonly ICallGateSubscriber<List<Vector3>, bool, object> move;
    private readonly ICallGateSubscriber<object> stop;
    private readonly ICallGateSubscriber<bool> ready, running;
    private readonly ICallGateSubscriber<List<Vector3>> waypoints;
    private readonly ICallGateSubscriber<float> getTolerance;
    private readonly ICallGateSubscriber<float, object> setTolerance;
    private float? originalTolerance;
    private bool ownsMovement, wasValid, manualStop;
    private int sentRevision = -1;
    private double lastFrame, lastTelemetry, nextAbilityAttempt, sentAt;
    private double recordFramesUntil;
    private List<Vector3>? sentPath;
    private StreamWriter? telemetry;
    private long telemetryBytes;
    private string previousStatus = "";
    private readonly JsonSerializerOptions json = new() { IncludeFields = true };

    public Plugin()
    {
        config = Pi.GetPluginConfig() as Configuration ?? new();
        if (config.Version < 2) { config.Version = 2; config.Enabled = false; }
        move = Pi.GetIpcSubscriber<List<Vector3>, bool, object>("vnavmesh.Path.MoveTo");
        stop = Pi.GetIpcSubscriber<object>("vnavmesh.Path.Stop");
        ready = Pi.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady");
        running = Pi.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning");
        waypoints = Pi.GetIpcSubscriber<List<Vector3>>("vnavmesh.Path.ListWaypoints");
        getTolerance = Pi.GetIpcSubscriber<float>("vnavmesh.Path.GetTolerance");
        setTolerance = Pi.GetIpcSubscriber<float, object>("vnavmesh.Path.SetTolerance");
        encounter.Trace = message => Record("state", message);
        try { events = new EventSource(Interop); }
        catch (Exception ex) { config.Enabled = false; Log.Error(ex, "ActionEffect hook unavailable"); Print("イベント受信の初期化に失敗。自動移動は無効です。"); }
        Commands.AddHandler("/dsrmeteor", new CommandInfo(OnCommand) { HelpMessage = "on / off / stop / status / kb on|off / record on|off" });
        Framework.Update += Update;
        Pi.UiBuilder.Draw += DrawUi;
        Pi.UiBuilder.OpenMainUi += OpenUi;
        Pi.UiBuilder.OpenConfigUi += OpenUi;
        Print($"v0.2.6: {(config.Enabled ? "自動移動有効" : "観測のみ")}。/dsrmeteor で設定画面。最初の塔まではPTの担当位置へ移動してください。");
    }
    private static void Print(string message) => Chat.Print("[DSRMeteorPilot] " + message);
    private void OnCommand(string command, string args)
    {
        var p = args.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        try
        {
            switch (p.FirstOrDefault())
            {
                case null:
                case "ui": OpenUi(); return;
                case "on":
                    if (events == null) throw new InvalidOperationException("イベント受信が利用できません");
                    if (encounter.Active || encounter.Phase == PilotPhase.Watching) throw new InvalidOperationException("ギミック途中では有効化できません。次のプルで有効化してください");
                    if (!ready.InvokeFunc()) throw new InvalidOperationException("vnavmeshが未導入または準備未完了");
                    config.Enabled = true; manualStop = false; encounter.Reset(); events.Clear(); Print("自動移動有効。南北・時計回り処理に対応します。"); break;
                case "off":
                    config.Enabled = false; manualStop = false;
                    StopMovement(); RestoreTolerance(); encounter.Reset(); events?.Clear();
                    Record("mode", new { Enabled = false }); Print("観測のみへ切り替えました。"); break;
                case "stop": manualStop = true; encounter.Abort("手動停止・このプルでは再開しません"); StopMovement(); RestoreTolerance(); Print(encounter.Status); break;
                case "kb":
                    if (p.Length != 2 || p[1] is not ("on" or "off")) throw new InvalidOperationException("kb on|off");
                    config.AutoAntiKnockback = p[1] == "on"; Print("自動ノックバック無効化: " + config.AutoAntiKnockback); break;
                case "record":
                    if (p.Length != 2 || p[1] is not ("on" or "off")) throw new InvalidOperationException("record on|off");
                    config.RecordTelemetry = p[1] == "on"; Print("診断記録: " + config.RecordTelemetry); break;
                case "status": Print($"{encounter.Status} / 自動={config.Enabled}, 着弾={encounter.ConfirmedHits}/7, 実測間隔={encounter.LastInterval:F3}s, 受信={events != null}"); break;
                default: Print("on: 有効化 / off: 観測のみ / stop: このプルの移動を中断 / status / kb on|off / record on|off"); break;
            }
            Pi.SavePluginConfig(config);
        }
        catch (Exception ex) { Print(ex.Message); }
    }
    private Frame Capture(double now)
    {
        var local = Objects.LocalPlayer;
        var valid = Client.IsLoggedIn && Client.TerritoryType == EncounterIds.Territory && !Client.IsPvP &&
            local != null && local.CurrentHp > 0 && Conditions[ConditionFlag.InCombat];
        if (!valid) return new(now, local?.EntityId ?? 0, local?.Position ?? default, false, [], [], [], false, 0, false);
        var actors = Objects.OfType<IBattleChara>().ToArray();
        var marked = actors.OfType<IPlayerCharacter>().Where(x => x.StatusList.Any(s => s.StatusId == EncounterIds.Prey))
            .Select(x => new MarkedPlayer(x.EntityId, x.Position)).ToArray();
        Tower[] Towers(uint action) => actors.Where(x => x.IsCasting && x.CastActionId == action)
            .Select(x => new Tower(x.EntityId, x.Position, now + Math.Max(0, x.TotalCastTime - x.CurrentCastTime))).ToArray();
        var kb = actors.Where(x => x.IsCasting && x.CastActionId == EncounterIds.Knockback)
            .Select(x => (double)Math.Max(0, x.TotalCastTime - x.CurrentCastTime)).DefaultIfEmpty(0).Max();
        return new(now, local!.EntityId, local.Position, true, marked, Towers(EncounterIds.FirstTower), Towers(EncounterIds.FinalTower),
            actors.Any(x => x.IsCasting && x.CastActionId == EncounterIds.CometCast), kb,
            local.StatusList.Any(s => s.StatusId is 1209 or 160 && s.RemainingTime > kb + 0.2));
    }
    private void Update(IFramework _)
    {
        try
        {
            var now = EventSource.Now;
            var f = Capture(now);
            if (events != null) events.AcceptEvents = f.Valid;
            if (!f.Valid)
            {
                StopMovement(); RestoreTolerance();
                if (wasValid)
                {
                    Record("end", encounter.Status); encounter.Reset(); events?.Clear(); manualStop = false;
                    telemetry?.Dispose(); telemetry = null; telemetryBytes = 0; recordFramesUntil = 0;
                }
                wasValid = false; lastFrame = now; return;
            }
            wasValid = true;
            if (f.CometCasting) recordFramesUntil = now + 30;
            if (encounter.Active && now - lastFrame > 0.35) encounter.Abort("フレーム停止・処理遅延");
            lastFrame = now;
            encounter.Observe(f);
            if (events != null)
            {
                if (events.Faulted) encounter.Abort("イベント受信エラーまたはキュー超過");
                var recordedEventFrame = false;
                while (events.TryRead(out var e))
                {
                    if (!recordedEventFrame) { Record("frame", f); recordedEventFrame = true; }
                    Record("effect", e); encounter.OnEffect(e);
                }
            }
            if (now <= recordFramesUntil && now - lastTelemetry >= 0.1) { Record("frame", f); lastTelemetry = now; }
            if (encounter.Status != previousStatus)
            {
                if (encounter.Phase is PilotPhase.Aborted or PilotPhase.Complete or PilotPhase.NotAssigned || encounter.Active) Print(encounter.Status);
                previousStatus = encounter.Status;
            }
            if (!config.Enabled || manualStop || !encounter.Active || !encounter.Goal.HasValue)
            { StopMovement(); RestoreTolerance(); return; }
            if (Keys[VirtualKey.W] || Keys[VirtualKey.A] || Keys[VirtualKey.S] || Keys[VirtualKey.D] || Keys[VirtualKey.UP] || Keys[VirtualKey.DOWN])
            { manualStop = true; encounter.Abort("手動移動キーを検出"); StopMovement(); RestoreTolerance(); Print(encounter.Status); return; }
            if (encounter.NeedsAntiKnockback && config.AutoAntiKnockback && now >= nextAbilityAttempt)
            { UseAntiKnockback(); nextAbilityAttempt = now + 0.5; }
            Drive(f);
        }
        catch (Exception ex)
        {
            encounter.Abort("制御エラー: " + ex.Message); StopMovement(); RestoreTolerance();
            Log.Error(ex, "Pilot update failed"); Print(encounter.Status);
        }
    }
    private void Drive(Frame f)
    {
        if (!ready.InvokeFunc()) throw new InvalidOperationException("vnavmesh準備状態が失われました");
        var goal = encounter.Goal!.Value;
        if (ownsMovement && running.InvokeFunc() && sentPath != null)
        {
            var active = waypoints.InvokeFunc();
            if (active.Count > 0 && Vector3.Distance(active[^1], sentPath[^1]) > 0.1f)
            {
                // A different plugin took control. Do not stop its new route.
                ownsMovement = false; throw new InvalidOperationException("他の移動制御との競合を検出");
            }
        }
        if (Route.Distance(f.Position, goal) <= 0.30f) { StopMovement(); sentRevision = encounter.GoalRevision; return; }
        if (sentRevision == encounter.GoalRevision)
        {
            if (!ownsMovement || (!running.InvokeFunc() && f.Time - sentAt > 0.25))
                throw new InvalidOperationException("vnavmeshの移動が到達前に停止");
            return;
        }
        if (!ownsMovement && running.InvokeFunc()) throw new InvalidOperationException("別のvnavmesh移動が実行中");
        originalTolerance ??= getTolerance.InvokeFunc();
        setTolerance.InvokeAction(0.15f);
        sentPath = encounter.Route!.Waypoints(f.Position, goal);
        ownsMovement = true;
        move.InvokeAction(sentPath, false);
        sentRevision = encounter.GoalRevision; sentAt = f.Time;
    }
    private static unsafe void UseAntiKnockback()
    {
        var player = Objects.LocalPlayer;
        if (player == null) return;
        var manager = ActionManager.Instance();
        if (manager == null) return;
        var job = player.ClassJob.RowId;
        var magic = job is 6 or 7 or 24 or 25 or 26 or 27 or 28 or 33 or 35 or 40 or 42;
        var action = magic ? 7559u : 7548u;
        if (manager->GetActionStatus(ActionType.Action, action) == 0)
            manager->UseAction(ActionType.Action, action, player.EntityId);
    }
    private void StopMovement()
    {
        if (!ownsMovement) return;
        try
        {
            var current = waypoints.InvokeFunc();
            if (current.Count == 0 || sentPath == null || Vector3.Distance(current[^1], sentPath[^1]) < 0.1f)
                stop.InvokeAction();
        }
        catch (Exception ex) { Log.Warning(ex, "Could not stop vnavmesh"); }
        finally { ownsMovement = false; }
    }
    private void RestoreTolerance()
    {
        if (!originalTolerance.HasValue) return;
        try { if (MathF.Abs(getTolerance.InvokeFunc() - 0.15f) < 0.001f) setTolerance.InvokeAction(originalTolerance.Value); }
        catch (Exception ex) { Log.Warning(ex, "Could not restore tolerance"); }
        finally { originalTolerance = null; }
    }
    private void Record(string kind, object data)
    {
        if (!config.RecordTelemetry || telemetryBytes >= 4 * 1024 * 1024) return;
        try
        {
            if (telemetry == null)
            {
                var directory = Path.Combine(Pi.GetPluginConfigDirectory(), "telemetry");
                Directory.CreateDirectory(directory);
                // Bounded retention without deleting any existing user data.
                if (Directory.GetFiles(directory, "pilot-*.jsonl").Length >= 20) { config.RecordTelemetry = false; Print("診断記録が20ファイルに達したため記録停止"); return; }
                telemetry = new StreamWriter(Path.Combine(directory, $"pilot-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}.jsonl")) { AutoFlush = true };
            }
            var line = JsonSerializer.Serialize(new { utc = DateTime.UtcNow, clock = EventSource.Now,
                enabled = config.Enabled, manualStop, kind, data }, json);
            telemetry.WriteLine(line); telemetryBytes += System.Text.Encoding.UTF8.GetByteCount(line) + 1;
        }
        catch (Exception ex) { config.RecordTelemetry = false; Log.Warning(ex, "Telemetry disabled"); }
    }
    public void Dispose()
    {
        Pi.UiBuilder.Draw -= DrawUi;
        Pi.UiBuilder.OpenMainUi -= OpenUi;
        Pi.UiBuilder.OpenConfigUi -= OpenUi;
        Framework.Update -= Update; Commands.RemoveHandler("/dsrmeteor");
        events?.Dispose(); StopMovement(); RestoreTolerance(); telemetry?.Dispose();
    }
}




