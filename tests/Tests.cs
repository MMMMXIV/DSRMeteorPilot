using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using DSRMeteorPilot;

public static class Tests
{
    public static string ReplayFixture()
    {
        var start=Point(90); var partner=Point(270);
        var options=new JsonSerializerOptions {IncludeFields=true};
        var records=new object[] {
            new {kind="frame",data=(object)F(-2,start,partner,Initial(start,partner))},
            new {kind="frame",data=(object)F(-0.01,start,partner,Initial(start,partner))},
            new {kind="effect",data=(object)new Effect(EncounterIds.FirstTower,100,1,start,[1],0)},
            new {kind="frame",data=(object)F(2.3,start,partner,[])}
        };
        return string.Join(Environment.NewLine,records.Select(x=>JsonSerializer.Serialize(x,options)));
    }
    static int checks;
    static void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); checks++; }
    static Vector3 Point(float angle, float r = 18) => new(100 + r * MathF.Cos(angle * MathF.PI / 180), 0, 100 + r * MathF.Sin(angle * MathF.PI / 180));
    static Tower[] Initial(Vector3 start, Vector3 partner) => [new(100,start,0), new(101,partner,0), new(102,new(82,0,100),0), new(103,new(118,0,100),0),
        new(104,new(104,0,104),0), new(105,new(96,0,104),0), new(106,new(96,0,96),0), new(107,new(104,0,96),0)];
    static Tower[] Final(double deadline) => Enumerable.Range(0,8).Select(i => new Tower((uint)(200+i), Point(i*45), deadline)).ToArray();
    static Frame F(double time, Vector3 pos, Vector3 partner, Tower[] first, double deadline = 13.4, bool immune = true) =>
        new(time, 1, pos, true, [new(1,pos),new(2,partner)], first, time >= 2 ? Final(deadline) : [], time < 0, 0, immune);
    static Encounter Start(Vector3 start, Vector3 partner)
    {
        var e = new Encounter(); var towers = Initial(start,partner);
        e.Observe(F(-2,start,partner,towers)); e.Observe(F(-0.01,start,partner,towers));
        e.OnEffect(new(EncounterIds.FirstTower,100,1,start,[1],0));
        Check(e.Active,"automatic start " + e.Status);
        return e;
    }
    static void Advance(ref Vector3 pos, List<Vector3> path, float distance)
    {
        while (path.Count > 0 && distance > 0)
        {
            float gap = Vector3.Distance(pos,path[0]);
            if (gap <= distance) { pos=path[0]; distance-=gap; path.RemoveAt(0); }
            else { pos=Vector3.Lerp(pos,path[0],distance/gap); distance=0; }
        }
    }
    static PilotPhase Simulate(float angle, float partnerAngle, double[] times, double finalTime, float speed=6, double latency=0.03, bool missing=false, bool duplicate=false, bool collision=false)
    {
        var start=Point(angle); var partner=Point(partnerAngle); var e=Start(start,partner); var route=e.Route!;
        var pos=start; var path=new List<Vector3>(); int revision=-1, hit=0; double allowMoveAt=0;
        const double dt=1.0/120;
        for(double now=dt; now<finalTime+1; now+=dt)
        {
            if(e.Goal.HasValue && e.GoalRevision!=revision)
            { path=route.Waypoints(pos,e.Goal.Value); revision=e.GoalRevision; allowMoveAt=now+latency; }
            if(e.Goal.HasValue && Route.Distance(pos,e.Goal.Value)<=0.30f) path.Clear();
            if(now>=allowMoveAt) Advance(ref pos,path,(float)dt*speed);
            e.Observe(F(now,pos,partner,[],finalTime));
            while(hit<7 && now>=times[hit])
            {
                if(!missing || hit!=2)
                {
                    var evt=new Effect(EncounterIds.CometHit,(uint)(300+hit),(uint)(10+hit),route.Drops[hit],[1,2,3,4],times[hit]);
                    if (collision)
                    {
                        var failure = evt with { Action=EncounterIds.CometFailure, Sequence=(uint)(80+hit) };
                        var priorGoal=e.Goal; var priorRevision=e.GoalRevision; var priorHits=e.ConfirmedHits;
                        e.OnEffect(failure); e.OnEffect(failure);
                        Check(e.Active && e.Goal==priorGoal && e.GoalRevision==priorRevision && e.ConfirmedHits==priorHits,
                            "collision and duplicate preserve route and count");
                    }
                    // Unrelated partner meteor must not advance the sequence.
                    e.OnEffect(evt with { Source=(uint)(400+hit), Sequence=(uint)(50+hit), Position=partner });
                    e.OnEffect(evt); if(duplicate)e.OnEffect(evt);
                }
                hit++;
            }
            if(e.Phase==PilotPhase.Aborted) return e.Phase;
            if(now>=finalTime)
            {
                var final=Point(route.FinishNorth?270:90);
                if(Route.Distance(pos,final)>0.6f) throw new Exception($"Late arrival {angle}/{partnerAngle}, distance={Route.Distance(pos,final)}, status={e.Status}");
                e.OnEffect(new(EncounterIds.FinalTower,200,100,final,[1],finalTime));
                return e.Phase;
            }
        }
        return e.Phase;
    }
    public static string Run(string timingJson)
    {
        checks=0;
        using var document=JsonDocument.Parse(timingJson);
        int simulations=0;
        foreach(var pull in document.RootElement.GetProperty("Pulls").EnumerateArray())
        {
            var times=pull.GetProperty("HitTimes").EnumerateArray().Select(x=>x.GetDouble()).ToArray();
            var final=pull.GetProperty("FinalTime").GetDouble();
            foreach(float north in new[]{240f,270f,300f}) foreach(float south in new[]{60f,90f,120f})
            foreach(bool swap in new[]{false,true})
            {
                float a=swap?south:north, b=swap?north:south;
                Check(Simulate(a,b,times,final,duplicate:true)==PilotPhase.Complete,$"replay timing {pull.GetProperty("Index")} {a}/{b}"); simulations++;
                Check(Simulate(a,b,times,final,duplicate:true,collision:true)==PilotPhase.Complete,$"collision continuation {pull.GetProperty("Index")} {a}/{b}"); simulations++;
            }
        }
        double[] nominal=Enumerable.Range(1,7).Select(i=>i*1.426).ToArray();
        Check(Simulate(90,270,nominal,13.4,speed:3)==PilotPhase.Aborted,"slow movement aborts");
        Check(Simulate(90,270,nominal,13.4,missing:true)==PilotPhase.Aborted,"missing effect aborts");
        Check(Simulate(90,270,nominal,13.4,latency:0.5)==PilotPhase.Aborted,"high latency aborts");
        var start=Point(90); var partner=Point(270);
        var e=Start(start,partner); var rev=e.GoalRevision;
        e.OnEffect(new(EncounterIds.FirstTower,100,1,start,[1],0)); Check(e.GoalRevision==rev,"tower duplicate ignored");
        e.Observe(F(0.1,start,partner,[]) with { Valid=false }); Check(e.Phase==PilotPhase.Aborted,"death aborts");
        e.Reset(); Check(e.Phase==PilotPhase.Idle && e.Goal==null && e.ConfirmedHits==0,"wipe reset clears all state");
        e=Start(start,partner); e.Observe(F(0.2,start,partner,[]) with { KnockbackRemaining=0.5,KnockbackImmune=false });
        Check(e.Phase==PilotPhase.Aborted,"missing anti-knockback aborts");
        e=Start(start,partner); e.Observe(F(0.2,start,partner,[]) with { KnockbackRemaining=3,KnockbackImmune=false });
        Check(e.NeedsAntiKnockback,"anti-knockback requested from observed cast");
        e=Start(start,partner); e.Observe(F(0.2,start,partner,[])); e.OnEffect(new(EncounterIds.CometFailure,99,4,start,[],0.2));
        Check(e.Phase==PilotPhase.Moving && e.Goal.HasValue && e.ConfirmedHits==0,"comet collision continues without counting a hit");
        e.Observe(F(0.3,start,partner,[]) with { Valid=false });
        Check(e.Phase==PilotPhase.Aborted,"death still stops after collision");
        e=Start(start,partner); e.Observe(F(2.3,start,partner,[])); Check(e.Phase==PilotPhase.Aborted,"no timer-based progress");
        e=Start(start,partner); e.Observe(F(0.2,start,partner,[]));
        e.OnEffect(new(EncounterIds.CometHit,300,8,new(float.NaN,0,100),[1],0.2));
        Check(e.Phase==PilotPhase.Aborted,"nonfinite event rejected");
        e=Start(start,partner); e.Observe(F(0.2,start,partner,[]));
        e.OnEffect(new(EncounterIds.CometHit,300,8,start,[1],-1));
        Check(e.ConfirmedHits==0,"stale effect ignored");
        e=Start(start,partner); e.Abort("manual stop"); e.Observe(F(0.2,start,partner,[]));
        e.OnEffect(new(EncounterIds.CometHit,300,8,start,[1],0.2));
        Check(e.Phase==PilotPhase.Aborted && e.Goal==null,"manual stop cannot resume from a hit");
        var late=new Encounter(); late.Observe(F(-0.1,start,partner,Initial(start,partner))); late.Observe(F(0,start,partner,Initial(start,partner)));
        late.OnEffect(new(EncounterIds.FirstTower,100,1,start,[1],0));
        Check(late.Phase==PilotPhase.Aborted,"late load cannot infer the beginning");
        e=Start(start,partner);
        for(int i=0;i<7;i++)
        {
            var position=e.Route!.Drops[Math.Min(i+1,6)];
            e.Observe(F(nominal[i],position,partner,[]));
            e.OnEffect(new(EncounterIds.CometHit,(uint)(300+i),(uint)(10+i),e.Route.Drops[i],[1],nominal[i]));
        }
        var finalGoal=Point(270); e.Observe(F(13.4,finalGoal,partner,[]));
        e.OnEffect(new(EncounterIds.FinalTower,200,100,finalGoal,[2],13.4));
        Check(e.Phase==PilotPhase.Aborted,"final tower must include local player");
        var notPrey=new Encounter(); notPrey.Observe(F(-2,start,partner,Initial(start,partner)) with {Marked=[new(2,partner),new(3,start)]});
        notPrey.Observe(F(0,start,partner,[])); notPrey.OnEffect(new(EncounterIds.FirstTower,100,1,start,[1],0));
        Check(!notPrey.Active,"non-prey never starts");
        notPrey.Observe(F(30,start,partner,[]));
        Check(notPrey.Phase==PilotPhase.NotAssigned,"non-prey does not become a tower timeout");
        var route=Route.Create(start,partner);
        var goal=route.Drops[2]; var beyond=Point(MathF.Atan2(goal.Z-100,goal.X-100)*180/MathF.PI+0.5f,19.5f);
        Check(route.Waypoints(beyond,goal).Count==1,"small overshoot does not make full lap");
        foreach(var bad in new[]{new Vector3(float.NaN,0,100),new Vector3(100,0,100),Point(0)})
        {
            bool rejected=false; try{Route.Create(bad,partner);}catch(ArgumentException){rejected=true;}catch(InvalidOperationException){rejected=true;}
            Check(rejected,"invalid geometry rejected");
        }
        return $"PASS: {checks} assertions; {simulations} full movement simulations across 17 synthetic timing sets and 18 NS assignments. Negative tests cover delay, duplicates, lost events, death, knockback, collision, wrong prey and reset. Synthetic movement, not live game validation.";
    }
}

