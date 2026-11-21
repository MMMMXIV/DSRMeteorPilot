using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
namespace DSRMeteorPilot;

public static class EncounterIds
{
    public const uint Territory = 968, Prey = 562, CometObject = 0x312F;
    public const uint FirstTower = 29564, FinalTower = 28651, CometCast = 25576;
    public const uint CometHit = 25577, CometFailure = 25578, Knockback = 25308;
    public static bool Relevant(uint action) => action is FirstTower or FinalTower or CometCast or CometHit or CometFailure or Knockback;
}
public readonly record struct Tower(uint Id, Vector3 Position, double ResolvesAt);
public readonly record struct MarkedPlayer(uint Id, Vector3 Position);
public readonly record struct Effect(uint Action, uint Source, uint Sequence, Vector3 Position, uint[] Targets, double Time);
public sealed record Frame(double Time, uint LocalId, Vector3 Position, bool Valid,
    MarkedPlayer[] Marked, Tower[] FirstTowers, Tower[] FinalTowers, bool CometCasting,
    double KnockbackRemaining, bool KnockbackImmune);
public enum PilotPhase { Idle, Watching, Moving, FinalTower, Complete, Aborted, NotAssigned }

/// <summary>Pure event state machine, called only on the framework thread.</summary>
public sealed class Encounter
{
    public PilotPhase Phase { get; private set; }
    public string Status { get; private set; } = "待機中";
    public Route? Route { get; private set; }
    public Vector3? Goal { get; private set; }
    public int GoalRevision { get; private set; }
    public int ConfirmedHits { get; private set; }
    public bool NeedsAntiKnockback { get; private set; }
    public bool Active => Phase is PilotPhase.Moving or PilotPhase.FinalTower;
    public double LastInterval { get; private set; }
    public Action<string>? Trace { get; set; }
    private Frame? frame, prepared;
    private readonly HashSet<(uint, uint)> seen = new();
    private readonly List<Vector3> actualMeteors = new();
    private double openedAt, startedAt, lastHitAt, firstSeenAt, finalDeadline;
    private uint watchedTower;
    private Vector3? finalGoal;
    private bool finalEventSeen;
    private int expectedSnapshot;
    public void Reset()
    {
        Phase = PilotPhase.Idle; Status = "待機中"; Route = null; Goal = null;
        frame = prepared = null; seen.Clear(); actualMeteors.Clear(); ConfirmedHits = 0;
        NeedsAntiKnockback = false; finalGoal = null; finalEventSeen = false;
        openedAt = startedAt = lastHitAt = firstSeenAt = finalDeadline = 0;
        watchedTower = 0; expectedSnapshot = 0; LastInterval = 0; GoalRevision++;
    }
    public void Abort(string reason)
    {
        Phase = PilotPhase.Aborted; Goal = null; NeedsAntiKnockback = false; GoalRevision++;
        Status = reason; Trace?.Invoke("abort: " + reason);
    }
    public void Observe(Frame value)
    {
        frame = value;
        if (!value.Valid) { if (Active) Abort("死亡・戦闘終了・エリア外"); return; }
        if (Phase == PilotPhase.Idle && value.CometCasting && value.FirstTowers.Length == 8)
        {
            Phase = PilotPhase.Watching; openedAt = firstSeenAt = value.Time;
            Status = "メテオ対象と初回塔を検出中"; Trace?.Invoke("window-open");
        }
        if (Phase == PilotPhase.Watching)
        {
            if (value.Marked.Length == 2 && value.Marked.All(x => x.Id != value.LocalId))
            {
                Phase = PilotPhase.NotAssigned; Status = "自分はメテオ対象外（自動移動なし）";
                Trace?.Invoke("not-assigned"); return;
            }
            if (value.Time - openedAt > 18) { Abort("初回塔の処理を検出できませんでした"); return; }
            if (value.Marked.Length == 2 && value.Marked.Any(x => x.Id == value.LocalId) && value.FirstTowers.Length == 8)
            {
                var self = NearTower(value.FirstTowers, value.Position);
                var partner = value.Marked.Single(x => x.Id != value.LocalId);
                var other = NearTower(value.FirstTowers, partner.Position);
                if (self.HasValue && other.HasValue && self.Value.Id != other.Value.Id)
                {
                    prepared = value; watchedTower = self.Value.Id;
                    Status = "初回塔と相方を捕捉・塔処理待ち";
                }
            }
        }
        if (!Active) return;
        if (value.Time - startedAt > 18) { Abort("処理全体のタイムアウト"); return; }
        if (Route!.RadiusError(value.Position) > 3.5f) { Abort("外周経路から逸脱"); return; }
        if (ConfirmedHits < 7 && value.Time - lastHitAt > 2.2) { Abort("メテオイベント欠落・遅延"); return; }
        if (value.FinalTowers.Length > 0)
        {
            var desired = value.FinalTowers.Where(x => MathF.Abs(x.Position.X - 100) < 0.75f &&
                (Route.FinishNorth ? x.Position.Z < 85 : x.Position.Z > 115)).ToArray();
            if (desired.Length == 1) { finalGoal = desired[0].Position; finalDeadline = desired[0].ResolvesAt; }
        }
        NeedsAntiKnockback = value.KnockbackRemaining is > 0 and <= 4.0 && !value.KnockbackImmune;
        if (NeedsAntiKnockback && value.KnockbackRemaining < 0.65) { Abort("ノックバック無効化を確認できませんでした"); return; }
        if (Phase == PilotPhase.FinalTower)
        {
            if (!finalGoal.HasValue) { Abort("最終塔の詠唱座標が見つかりません"); return; }
            if (!Goal.HasValue || Vector3.Distance(Goal.Value, finalGoal.Value) > 0.1f) SetGoal(finalGoal.Value);
            var distance = Route.ArcLength(value.Position, finalGoal.Value);
            if (distance / Route.RunSpeed > finalDeadline - value.Time + 0.15 && Route.Distance(value.Position, finalGoal.Value) > 0.6f)
                Abort("最終塔の処理時刻までに到達できません");
            else if (!finalEventSeen && value.Time > finalDeadline + 1.0) Abort("最終塔の処理イベントを確認できませんでした");
        }
    }
    public void OnEffect(Effect e)
    {
        if (frame == null || !frame.Valid || e.Time < frame.Time - 0.5 || e.Time > frame.Time + 0.1) return;
        if (!double.IsFinite(e.Time) || !float.IsFinite(e.Position.X) || !float.IsFinite(e.Position.Y) || !float.IsFinite(e.Position.Z))
        { if (Active) Abort("イベント座標・時刻が不正"); return; }
        if (!seen.Add((e.Source, e.Sequence))) return;
        if (seen.Count > 256) { Abort("イベント数が想定を超えました"); return; }
        if (Phase == PilotPhase.Watching && e.Action == EncounterIds.FirstTower && e.Source == watchedTower && e.Targets.Contains(frame.LocalId))
        {
            if (prepared == null || e.Time - prepared.Time > 0.35 || e.Time - firstSeenAt < 0.5)
            { Abort("開始前の塔・対象者情報が不足または古すぎます"); return; }
            try
            {
                var partner = prepared.Marked.Single(x => x.Id != frame.LocalId);
                Route = Route.Create(prepared.Position, partner.Position);
                Phase = PilotPhase.Moving; startedAt = lastHitAt = e.Time;
                ConfirmedHits = 0; expectedSnapshot = 1; SetGoal(Route.Drops[1]);
                Status = Route.Gg ? "GG経路・イベント同期" : "通常経路・イベント同期";
                Trace?.Invoke($"start: gg={Route.Gg}, short={Route.Short}, radius={Route.Radius:F2}");
            }
            catch (Exception ex) { Abort(ex.Message); }
            return;
        }
        if (!Active) return;
        if (e.Action == EncounterIds.CometFailure)
        {
            // Collision is diagnostic only. It is not a placement tick and must
            // not reset the goal, increment the hit count, or alter timing.
            Status = "メテオ衝突イベントを検出（予定経路を続行）";
            Trace?.Invoke("collision: continuing planned route");
            return;
        }
        if (e.Action == EncounterIds.FinalTower && finalGoal.HasValue && DSRMeteorPilot.Route.Distance(e.Position, finalGoal.Value) < 1.0f)
        {
            finalEventSeen = true;
            if (!e.Targets.Contains(frame.LocalId)) { Abort("最終塔の対象者に自分が含まれていません"); return; }
            if (ConfirmedHits != 7) { Abort("最終塔処理時点でメテオ7回を確認できませんでした"); return; }
            Phase = PilotPhase.Complete; Goal = null; NeedsAntiKnockback = false; GoalRevision++;
            Status = "自分のメテオ7回と最終塔処理を確認"; Trace?.Invoke("complete"); return;
        }
        if (e.Action != EncounterIds.CometHit || ConfirmedHits >= 7) return;
        var expected = Route!.Drops[ConfirmedHits];
        // Both comets damage multiple targets. Associate by source location, never by victim or alternating packets.
        if (Route.Distance(e.Position, expected) > 2.0f)
        {
            if (Phase == PilotPhase.Moving && Goal.HasValue && Route.Distance(e.Position, Goal.Value) < 5.1f)
                Abort("相方のメテオが次の予定地点に接近");
            return;
        }
        if (actualMeteors.Any(p => Route.Distance(p, e.Position) < 5.1f)) { Abort("自分のメテオ間隔が不足"); return; }
        var interval = e.Time - lastHitAt;
        if (interval < 1.0 || interval > 1.9) { Abort("着弾の間隔が想定と異なるため停止しました"); return; }
        LastInterval = interval; lastHitAt = e.Time; actualMeteors.Add(e.Position); ConfirmedHits++;
        Trace?.Invoke($"hit: {ConfirmedHits}, interval={interval:F3}, x={e.Position.X:F2}, z={e.Position.Z:F2}");
        if (ConfirmedHits < 7 && Route.Distance(frame.Position, Route.Drops[expectedSnapshot]) > 0.75f)
        { Abort("次のメテオ位置確定時に予定地点へ未到達"); return; }
        if (ConfirmedHits < 6) { expectedSnapshot = ConfirmedHits + 1; SetGoal(Route.Drops[expectedSnapshot]); }
        else if (ConfirmedHits == 6)
        {
            // Sixth impact snapshots seventh placement. Leave now, verify seventh impact separately.
            Phase = PilotPhase.FinalTower;
            if (!finalGoal.HasValue) { Abort("最終塔の詠唱座標が見つかりません"); return; }
            SetGoal(finalGoal.Value); Status = "最終塔へ移動・7回目の着弾確認待ち";
        }
    }
    private void SetGoal(Vector3 p) { Goal = p; GoalRevision++; }
    private static Tower? NearTower(Tower[] towers, Vector3 p)
    {
        var near = towers.Where(t => Route.Distance(t.Position, p) <= 3.0f && Route.Distance(t.Position, new(100, p.Y, 100)) > 15).ToArray();
        return near.Length == 1 ? near[0] : null;
    }
}
