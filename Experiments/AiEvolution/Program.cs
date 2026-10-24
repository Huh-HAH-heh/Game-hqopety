using System.Numerics;
using System.Text.Json;
using Core.Items;
using Core.Map;
using Core.Unit;

internal static class Program
{
    private const int GeneCount = 21;
    private const float Dt = 0.05f;

    private static readonly float[] Min = [.65f,.55f,.65f,.70f,.35f,.50f,.50f,.50f,.50f,.50f,.45f,.03f,.04f,.12f,.12f,.35f,12f,.08f,.15f,.60f,.20f];
    private static readonly float[] Max = [1.35f,1.45f,1.45f,1.40f,1.25f,1.50f,1.50f,1.50f,1.50f,.85f,.15f,.18f,.34f,.34f,.85f,40f,.40f,.50f,.95f,.60f];
    private static readonly float[] BaseGenes = [1f,1f,1f,1f,1f,1f,1f,1f,1f,1f,.60f,.08f,.10f,.23f,.24f,.66f,24f,.22f,.35f,.84f,.35f];

    private static int Main(string[] args)
    {
        try
        {
            int popSize=Arg(args,"--population",8,4,128), generations=Arg(args,"--generations",4,1,10000);
            int battles=Arg(args,"--battles",6,2,1000), ticks=Arg(args,"--ticks",400,100,100000);
            int team=Arg(args,"--team-size",4,2,16), seed=Arg(args,"--seed",20261024,int.MinValue,int.MaxValue);
            string output=Path.GetFullPath(TextArg(args,"--output","ai-balance-best.json"));
            string dir=Path.GetDirectoryName(output)!; Directory.CreateDirectory(dir);
            string csv=Path.Combine(dir,Path.GetFileNameWithoutExtension(output)+".history.csv");
            string? seedPath=TextOpt(args,"--seed-profile");
            AiBalanceProfile seedProfile=seedPath==null ? AiBalanceProfile.Default : LoadProfile(Path.GetFullPath(seedPath));
            float[] seedGenes=GenesFrom(seedProfile);
            float[] mutationBase=seedPath==null ? BaseGenes : seedGenes;
            var rng=new Random(seed);
            var population=new List<float[]>(popSize) { (float[])BaseGenes.Clone() };
            if(seedPath!=null && popSize>1) population.Add(seedGenes);
            while(population.Count<popSize) population.Add(Mutate(mutationBase,rng,.75,.22f));

            double globalBest=double.NegativeInfinity;
            float[] globalGenes=(float[])BaseGenes.Clone();
            using var history=new StreamWriter(csv,false);
            history.WriteLine("generation,best_fitness,mean_fitness,engage,flank,cover,retreat,prone,suppressive_fire");
            Console.WriteLine($"AI evolution: population={popSize}; generations={generations}; battles={battles}; ticks={ticks}; team={team}v{team}");

            for(int gen=1;gen<=generations;gen++)
            {
                var results=new List<(float[] Genes,double Fitness)>(popSize);
                for(int i=0;i<population.Count;i++)
                {
                    double fitness=Evaluate(Profile(population[i]),battles,ticks,team);
                    results.Add((population[i],fitness));
                    Console.WriteLine($"gen={gen}/{generations} candidate={i+1}/{popSize} fitness={fitness:F2}");
                }
                results.Sort((a,b)=>b.Fitness.CompareTo(a.Fitness));
                double mean=results.Average(x=>x.Fitness);
                if(results[0].Fitness>globalBest) { globalBest=results[0].Fitness; globalGenes=(float[])results[0].Genes.Clone(); }
                File.WriteAllText(output,JsonSerializer.Serialize(Profile(globalGenes),new JsonSerializerOptions{WriteIndented=true}));
                history.WriteLine($"{gen},{results[0].Fitness:F3},{mean:F3},{results[0].Genes[0]:F4},{results[0].Genes[1]:F4},{results[0].Genes[2]:F4},{results[0].Genes[3]:F4},{results[0].Genes[4]:F4},{results[0].Genes[5]:F4}");
                history.Flush();
                Console.WriteLine($"GENERATION {gen}: best={results[0].Fitness:F2}; mean={mean:F2}; best-ever={globalBest:F2}");
                if(gen<generations) population=Next(results,popSize,rng);
            }
            Console.WriteLine($"Saved profile: {output}"); Console.WriteLine($"History: {csv}");
            return 0;
        }
        catch(Exception ex) { Console.Error.WriteLine($"AI evolution failed: {ex.Message}"); return 1; }
    }

    private static List<float[]> Next(List<(float[] Genes,double Fitness)> rank,int count,Random r)
    {
        var next=new List<float[]>(count) { (float[])rank[0].Genes.Clone() };
        if(count>1) next.Add((float[])rank[1].Genes.Clone());
        int pool=Math.Clamp(count/2,2,rank.Count);
        while(next.Count<count)
        {
            if(r.NextDouble()<.06) { next.Add(Mutate(BaseGenes,r,.9,.45f)); continue; }
            float[] a=rank[r.Next(pool)].Genes,b=rank[r.Next(pool)].Genes,g=new float[GeneCount];
            for(int j=0;j<GeneCount;j++) g[j]=r.Next(2)==0?a[j]:b[j];
            next.Add(Mutate(g,r,.18,.14f));
        }
        return next;
    }

    private static float[] Mutate(float[] parent,Random r,double chance,float strength)
    {
        float[] g=(float[])parent.Clone();
        for(int i=0;i<g.Length;i++)
            if(r.NextDouble()<chance) g[i]=Math.Clamp(g[i]+((float)r.NextDouble()*2f-1f)*(Max[i]-Min[i])*strength,Min[i],Max[i]);
        return g;
    }

    private static float[] GenesFrom(AiBalanceProfile p) =>
    [
        p.EngageScale,p.FlankScale,p.CoverScale,p.RetreatScale,p.ProneScale,
        p.SuppressiveFireScale,p.LastKnownScale,p.InvestigationScale,p.SquadContactScale,p.PatrolScale,
        p.ProneAdoptionMinimumUtility,p.FlankUtilityAdvantage,p.RegularSwitchMargin,p.CommittedSwitchMargin,
        p.MinimumInvestigationUtility,p.TacticalCoverPressureWeight,p.TacticalFlankDistanceStart,
        p.TacticalFlankDistanceWeight,p.InvestigationDistancePenalty,p.VisibleCombatMediumScore,p.CommitDurationBaseSeconds
    ];

    private static AiBalanceProfile LoadProfile(string path)
    {
        using JsonDocument doc=JsonDocument.Parse(File.ReadAllText(path));
        JsonElement root=doc.RootElement;
        if(root.ValueKind==JsonValueKind.Object &&
            (root.TryGetProperty("profile",out JsonElement nested) || root.TryGetProperty("Profile",out nested)))
            root=nested;
        return JsonSerializer.Deserialize<AiBalanceProfile>(root.GetRawText(),new JsonSerializerOptions
        { PropertyNameCaseInsensitive=true })
            ?? throw new InvalidDataException($"No AiBalanceProfile in {path}.");
    }

    private static AiBalanceProfile Profile(float[] g) => AiBalanceProfile.Default with
    {
        EngageScale=g[0], FlankScale=g[1], CoverScale=g[2], RetreatScale=g[3], ProneScale=g[4],
        SuppressiveFireScale=g[5], LastKnownScale=g[6], InvestigationScale=g[7], SquadContactScale=g[8],
        PatrolScale=g[9], ProneAdoptionMinimumUtility=g[10], FlankUtilityAdvantage=g[11],
        RegularSwitchMargin=g[12], CommittedSwitchMargin=g[13], MinimumInvestigationUtility=g[14],
        TacticalCoverPressureWeight=g[15], TacticalFlankDistanceStart=g[16], TacticalFlankDistanceWeight=g[17],
        InvestigationDistancePenalty=g[18], VisibleCombatMediumScore=g[19],
        CommitDurationBaseSeconds=g[20]
    };

    private static double Evaluate(AiBalanceProfile p,int battles,int ticks,int team)
    {
        double total=0;
        for(int i=0;i<battles;i++) total+=Battle(p,i%2==0,(i/2)%3,ticks,team);
        return total/battles;
    }

    private static double Battle(AiBalanceProfile candidate,bool leftCandidate,int scenario,int ticks,int team)
    {
        var world=new WorldMap(regionsX:8,regionsY:4,layerCount:32);
        if(scenario==1) Barrier(world);
        var sim=new UnitSimulation(unitCapacity:team*2+4,bodyCapacity:2048);
        sim.AI.SetBalanceProfileForFaction(1,leftCandidate?candidate:AiBalanceProfile.Default);
        sim.AI.SetBalanceProfileForFaction(2,leftCandidate?AiBalanceProfile.Default:candidate);
        Spawn(sim,true,1,scenario,team); Spawn(sim,false,2,scenario,team); sim.AI.Enabled=true;
        Team left=ReadTeam(sim,1),right=ReadTeam(sim,2);
        for(int t=0;t<ticks;t++)
        {
            sim.Update(world,Dt); left=ReadTeam(sim,1); right=ReadTeam(sim,2);
            if(left.Alive==0||right.Alive==0) break;
        }
        Team own=leftCandidate?left:right, enemy=leftCandidate?right:left;
        double outcome=own.Alive>enemy.Alive?1:own.Alive<enemy.Alive?0:
            own.Hp>enemy.Hp+.03?.75:own.Hp+.03<enemy.Hp?.25:.5;
        return 45* outcome+20*own.Hp+20*(1-enemy.Hp)+15d*own.Alive/Math.Max(1,own.Total);
    }

    private static void Spawn(UnitSimulation sim,bool left,ushort faction,int scenario,int count)
    {
        for(int i=0;i<count;i++)
        {
            int y=9+i*46/Math.Max(1,count-1),offset=(i%2)*2;
            int x=scenario switch { 2=>left?33+offset:95-offset, 1=>left?16+offset:112-offset, _=>left?10+offset:118-offset };
            UnitId id=sim.Spawn(UnitType.Colonist,new Vector3(x,y,.1f),bodyAngle:left?0f:MathF.PI,factionTag:faction);
            var weapon=i%3==0?WeaponCatalog.AssaultRifle:WeaponCatalog.Pistol;
            int slot=sim.AddInventoryItem(id,weapon);
            if(slot<0||!sim.EquipWeapon(id,slot,UnitWeaponSlot.Primary)) throw new InvalidOperationException($"Could not equip {weapon.Name}.");
        }
    }

    private static void Barrier(WorldMap world)
    {
        for(int y=4;y<60;y++)
        {
            if((y>=18&&y<=24)||(y>=40&&y<=46)) continue;
            world.SetNavigationBlocked(64,y,true);
            for(int z=0;z<16;z++) world.SetMaterialId(64,y,z,(ushort)1);
        }
    }

    private static Team ReadTeam(UnitSimulation sim,ushort faction)
    {
        int total=0,alive=0; double hp=0,max=0;
        foreach(int i in sim.Units.ActiveIndices)
        {
            if(sim.Units.FactionTag[i]!=faction) continue;
            total++; float m=MathF.Max(0,sim.Health.OverallMaxHitPoints[i]);
            float h=Math.Clamp(sim.Health.OverallHitPoints[i],0,m); max+=m; hp+=h; if(h>0) alive++;
        }
        return new Team(total,alive,max>0?Math.Clamp(hp/max,0,1):0);
    }

    private static int Arg(string[] a,string key,int fallback,int min,int max)
    {
        string? s=TextOpt(a,key); if(s==null) return fallback;
        if(!int.TryParse(s,out int n)||n<min||n>max) throw new ArgumentOutOfRangeException(key,$"Expected {min}..{max}");
        return n;
    }
    private static string TextArg(string[] a,string key,string fallback)=>TextOpt(a,key)??fallback;
    private static string? TextOpt(string[] a,string key)
    {
        for(int i=0;i<a.Length;i++) if(string.Equals(a[i],key,StringComparison.OrdinalIgnoreCase))
        { if(i+1>=a.Length) throw new ArgumentException($"Missing value for {key}"); return a[i+1]; }
        return null;
    }
    private readonly record struct Team(int Total,int Alive,double Hp);
}
