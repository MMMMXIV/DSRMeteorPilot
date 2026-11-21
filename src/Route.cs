using System;
using System.Collections.Generic;
using System.Numerics;
namespace DSRMeteorPilot;

public sealed class Route
{
    public const float RunSpeed = 6f;
    public Vector3[] Drops { get; private init; } = [];
    public float Radius { get; private init; }
    public bool Gg { get; private init; }
    public bool Short { get; private init; }
    public bool FinishNorth { get; private init; }
    private static readonly Vector3 Center = new(100, 0, 100);
    private const float Tau = 2 * MathF.PI;
    private static float Angle(Vector3 p) => MathF.Atan2(p.Z - 100, p.X - 100);
    public static float Distance(Vector3 a, Vector3 b) => Vector2.Distance(new(a.X, a.Z), new(b.X, b.Z));
    private static float Cw(float from, float to) => ((to - from) % Tau + Tau) % Tau;
    private static bool Finite(Vector3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);
    public static Route Create(Vector3 start, Vector3 partner)
    {
        if (!Finite(start) || !Finite(partner)) throw new ArgumentException("非有限座標");
        var startRadius = Distance(start, Center);
        const float radius = 19.5f;
        var partnerRadius = Distance(partner, Center);
        if (startRadius < 17 || startRadius > 20.5f || partnerRadius < 17 || partnerRadius > 20.5f)
            throw new InvalidOperationException("開始位置が対応する外周範囲から外れています");
        if (MathF.Abs(start.Z - 100) < 13 || MathF.Abs(partner.Z - 100) < 13 || (start.Z < 100) == (partner.Z < 100))
            throw new InvalidOperationException("南北に分かれたメテオ配置ではありません");
        var angle = Angle(start);
        var gap = Cw(angle, Angle(partner));
        var clearance = 2 * MathF.Asin(6.0f / (2 * MathF.Min(startRadius, partnerRadius)));
        var span = MathF.Min(132 * MathF.PI / 180, gap - clearance);
        if (span <= 0) throw new InvalidOperationException("経路の角度が不足");
        var drops = new Vector3[7];
        drops[0] = start;
        for (var i = 1; i < 7; i++)
            drops[i] = new(100 + radius * MathF.Cos(angle + span * i / 6), start.Y, 100 + radius * MathF.Sin(angle + span * i / 6));
        for (var i = 0; i < drops.Length; i++)
        {
            if (Distance(drops[i], partner) < 5.5f) throw new InvalidOperationException("相方のメテオとの距離不足");
            for (var j = 0; j < i; j++)
                if (Distance(drops[i], drops[j]) < 5.5f) throw new InvalidOperationException("メテオ間隔不足・このGG配置は未対応");
        }
        if (radius * span / 6 / RunSpeed > 1.25f) throw new InvalidOperationException("移動に必要な時間が長すぎます");
        return new Route { Drops = drops, Radius = radius, Gg = MathF.Min(gap, Tau-gap) < 135 * MathF.PI / 180,
            Short = gap < 135 * MathF.PI / 180, FinishNorth = start.Z > 100 };
    }
    public float RadiusError(Vector3 p) => MathF.Abs(Distance(p, Center) - Radius);
    public float ArcLength(Vector3 from, Vector3 to)
    {
        var arc = Cw(Angle(from), Angle(to));
        if (arc > Tau - 0.08f) arc = 0;
        return arc * Radius + MathF.Abs(Distance(to, Center) - Distance(from, Center));
    }
    public List<Vector3> Waypoints(Vector3 from, Vector3 to)
    {
        if (!Finite(from) || !Finite(to)) throw new ArgumentException("非有限経路");
        var arc = Cw(Angle(from), Angle(to));
        if (arc > Tau - 0.08f) arc = 0;
        if (arc > MathF.PI) throw new InvalidOperationException("半周を超える移動命令を拒否");
        var count = Math.Max(1, (int)MathF.Ceiling(arc / (5 * MathF.PI / 180)));
        var result = new List<Vector3>(count);
        for (var i = 1; i <= count; i++)
        {
            var angle = Angle(from) + arc * i / count;
            result.Add(new(100 + Radius * MathF.Cos(angle), from.Y, 100 + Radius * MathF.Sin(angle)));
        }
        result[^1] = to;
        return result;
    }
}
