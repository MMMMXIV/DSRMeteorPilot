using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
namespace DSRMeteorPilot;

/// <summary>Copy relevant native event data immediately. No game objects survive the callback.</summary>
public sealed unsafe class EventSource : IDisposable
{
    private readonly Hook<ActionEffectHandler.Delegates.Receive> hook;
    private readonly ConcurrentQueue<Effect> queue = new();
    public volatile bool AcceptEvents;
    public volatile bool Faulted;
    public static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
    public EventSource(IGameInteropProvider interop)
    {
        var address = (nint)ActionEffectHandler.Addresses.Receive.Value;
        if (address == 0) throw new InvalidOperationException("ActionEffect受信アドレスを解決できません");
        hook = interop.HookFromAddress<ActionEffectHandler.Delegates.Receive>(address, Receive);
        try { hook.Enable(); } catch { hook.Dispose(); throw; }
    }
    private void Receive(uint caster, Character* actor, Vector3* targetPosition,
        ActionEffectHandler.Header* header, ActionEffectHandler.TargetEffects* effects, GameObjectId* targets)
    {
        try
        {
            if (AcceptEvents && header != null && actor != null && header->ActionType == 1 && EncounterIds.Relevant(header->ActionId))
            {
                if (header->NumTargets > 64 || (header->NumTargets > 0 && targets == null) || queue.Count >= 128) Faulted = true;
                else
                {
                    var ids = new uint[header->NumTargets];
                    for (var i = 0; i < ids.Length; i++) ids[i] = (uint)(ulong)targets[i];
                    queue.Enqueue(new(header->ActionId, caster, header->GlobalSequence, actor->Position, ids, Now));
                }
            }
        }
        catch { Faulted = true; }
        // Always forward the original exactly once, including if our recorder failed.
        hook.Original(caster, actor, targetPosition, header, effects, targets);
    }
    public bool TryRead(out Effect e) => queue.TryDequeue(out e);
    public void Clear() { while (queue.TryDequeue(out _)) { } Faulted = false; }
    public void Dispose() { AcceptEvents = false; hook.Dispose(); Clear(); }
}
