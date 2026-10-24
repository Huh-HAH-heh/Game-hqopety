using System.Numerics;
using System.Text.Json;
using Core.AI;
using Core.Items;
using Core.Map;
using Core.Unit;
using World;

internal static class Program
{
    private const int GeneCount = 40;
    private const float Dt = 0.05f;
    private static AiBalanceProfile _profileBase = AiBalanceProfile.Default;

    private static readonly float[] Min = [.65f,.55f,.65f,.70f,.35f,.50f,.50f,.50f,.50f,.50f,.45f,.03f,.04f,.12f,.12f,.35f,12f,.08f,.15f,.60f,.20f,.60f,.25f,.01f,.80f,.10f,4f,4f,5f,4f,5f,.75f,.80f,.04f,.04f,.50f,.30f,.02f,.003f,.20f];
    private static readonly float[] Max = [1.35f,1.45f,1.45f,1.40f,1.25f,1.50f,1.50f,1.50f,1.50f,1.50f,.85f,.15f,.18f,.34f,.34f,.85f,40f,.40f,.50f,.95f,.60f,.95f,3f,.08f,1f,.45f,14f,14f,20f,24f,30f,.98f,.999f,.30f,.35f,.95f,.80f,.16f,.04f,1.50f];
    private static readonly float[] BaseGenes = [1f,1f,1f,1f,1f,1f,1f,1f,1f,1f,.60f,.08f,.10f,.23f,.24f,.66f,24f,.22f,.35f,.84f,.35f,.84f,1f,.03f,.94f,.20f,8f,4f,6f,6f,8f,.90f,.98f,.12f,.16f,.80f,.60f,.08f,.012f,.90f];

    private static int Main(string[] args)
    {
        try
        {
            ValidateGenomeConfiguration();
            int popSize=Arg(args,"--population",8,4,128), generations=Arg(args,"--generations",4,1,10000);
            int battles=Arg(args,"--battles",8,2,1000), ticks=Arg(args,"--ticks",600,100,100000);
            int validationBattles=Arg(args,"--validation-battles",Math.Max(8,battles),8,1000);
            int team=Arg(args,"--team-size",4,2,16), seed=Arg(args,"--seed",20261024,int.MinValue,int.MaxValue);
            string output=Path.GetFullPath(TextArg(args,"--output","Experiments/AiEvolution/output/ai-balance-best.json"));
            string dir=Path.GetDirectoryName(output)!; Directory.CreateDirectory(dir);
            string csv=Path.Combine(dir,Path.GetFileNameWithoutExtension(output)+".history.csv");
            string? seedPath=TextOpt(args,"--seed-profile");
            AiBalanceProfile seedProfile=seedPath==null ? AiBalanceProfile.Default : LoadProfile(Path.GetFullPath(seedPath));
            _profileBase=seedProfile;
            float[] seedGenes=GenesFrom(seedProfile);
            float[] mutationBase=seedPath==null ? BaseGenes : seedGenes;
            var rng=new Random(seed);
            var population=new List<float[]>(popSize) { (float[])mutationBase.Clone() };
            while(population.Count<popSize) population.Add(Mutate(mutationBase,rng,.75,.22f));

            double globalBest=double.NegativeInfinity;
            float[] globalGenes=(float[])mutationBase.Clone();
            using var history=new StreamWriter(csv,false);
            history.WriteLine("generation,best_fitness,mean_fitness,win_rate,draw_rate,survivor_rate,own_health_ratio,enemy_health_ratio,enemy_kills,engage,flank,cover,retreat,prone,suppressive_fire,open_score,barrier_score,close_score,canyon_score,open_win_rate,barrier_win_rate,close_win_rate,canyon_win_rate");
            Console.WriteLine($"AI evolution: population={popSize}; generations={generations}; battles={battles}; ticks={ticks}; team={team}v{team}");

            for(int gen=1;gen<=generations;gen++)
            {
                var results=new List<(float[] Genes,Evaluation Metrics)>(popSize);
                for(int i=0;i<population.Count;i++)
                {
                    Evaluation metrics=Evaluate(Profile(population[i]),battles,ticks,team,seed);
                    results.Add((population[i],metrics));
                    Console.WriteLine($"gen={gen}/{generations} candidate={i+1}/{popSize} " +
                        $"fitness={metrics.Fitness:F2}; wins={metrics.WinRate:P0}; survivors={metrics.SurvivorRate:P0}");
                }
                results.Sort((a,b)=>b.Metrics.Fitness.CompareTo(a.Metrics.Fitness));
                double mean=results.Average(x=>x.Metrics.Fitness);
                var champion=results[0];
                if(champion.Metrics.Fitness>globalBest)
                {
                    globalBest=champion.Metrics.Fitness;
                    globalGenes=(float[])champion.Genes.Clone();
                }
                File.WriteAllText(output,JsonSerializer.Serialize(Profile(globalGenes),new JsonSerializerOptions{WriteIndented=true}));
                history.WriteLine($"{gen},{champion.Metrics.Fitness:F3},{mean:F3}," +
                    $"{champion.Metrics.WinRate:F4},{champion.Metrics.DrawRate:F4}," +
                    $"{champion.Metrics.SurvivorRate:F4},{champion.Metrics.OwnHealthRatio:F4}," +
                    $"{champion.Metrics.EnemyHealthRatio:F4},{champion.Metrics.AverageEnemyKills:F3}," +
                    $"{champion.Genes[0]:F4},{champion.Genes[1]:F4},{champion.Genes[2]:F4}," +
                    $"{champion.Genes[3]:F4},{champion.Genes[4]:F4},{champion.Genes[5]:F4}," +
                    $"{champion.Metrics.Scenarios[0].AverageFitness:F3},{champion.Metrics.Scenarios[1].AverageFitness:F3}," +
                    $"{champion.Metrics.Scenarios[2].AverageFitness:F3},{champion.Metrics.Scenarios[3].AverageFitness:F3}," +
                    $"{champion.Metrics.Scenarios[0].WinRate:F4},{champion.Metrics.Scenarios[1].WinRate:F4}," +
                    $"{champion.Metrics.Scenarios[2].WinRate:F4},{champion.Metrics.Scenarios[3].WinRate:F4}");
                history.Flush();
                Console.WriteLine($"GENERATION {gen}: best={champion.Metrics.Fitness:F2}; mean={mean:F2}; " +
                    $"wins={champion.Metrics.WinRate:P1}; survivor-rate={champion.Metrics.SurvivorRate:P1}; " +
                    $"health={champion.Metrics.OwnHealthRatio:P1}; best-ever={globalBest:F2}");
                if(gen<generations)
                    population=Next(results.Select(x=>(x.Genes,x.Metrics.Fitness)).ToList(),popSize,rng,mutationBase);
            }
            int validationSeed=unchecked(seed^0x5F3759DF);
            Evaluation trained=Evaluate(Profile(globalGenes),validationBattles,ticks,team,validationSeed);
            Evaluation baseline=Evaluate(AiBalanceProfile.Default,validationBattles,ticks,team,validationSeed);
            string validationPath=Path.Combine(dir,Path.GetFileNameWithoutExtension(output)+".validation.json");
            var validationReport=new
            {
                TrainingSeed=seed,
                ValidationSeed=validationSeed,
                BattlesPerProfile=validationBattles,
                Baseline=baseline,
                Trained=trained,
                Delta=new
                {
                    Fitness=trained.Fitness-baseline.Fitness,
                    WinRate=trained.WinRate-baseline.WinRate,
                    SurvivorRate=trained.SurvivorRate-baseline.SurvivorRate,
                    OwnHealthRatio=trained.OwnHealthRatio-baseline.OwnHealthRatio,
                    EnemyHealthDamage=baseline.EnemyHealthRatio-trained.EnemyHealthRatio
                },
                ImprovesFitness=trained.Fitness>baseline.Fitness,
                ImprovesWinRate=trained.WinRate>baseline.WinRate
            };
            File.WriteAllText(validationPath,JsonSerializer.Serialize(validationReport,new JsonSerializerOptions{WriteIndented=true}));
            Console.WriteLine($"HOLDOUT baseline: score={baseline.Fitness:F2}; wins={baseline.WinRate:P1}; survivor-rate={baseline.SurvivorRate:P1}");
            Console.WriteLine($"HOLDOUT trained:  score={trained.Fitness:F2}; wins={trained.WinRate:P1}; survivor-rate={trained.SurvivorRate:P1}");
            Console.WriteLine($"HOLDOUT delta:    score={trained.Fitness-baseline.Fitness:+0.00;-0.00;0.00}; wins={trained.WinRate-baseline.WinRate:+0.0%;-0.0%;0.0%}");
            for(int i=0;i<trained.Scenarios.Length;i++)
            {
                ScenarioMetrics b=baseline.Scenarios[i],t=trained.Scenarios[i];
                Console.WriteLine($"HOLDOUT {t.Name}: baseline={b.AverageFitness:F2}/{b.WinRate:P0} wins; " +
                    $"trained={t.AverageFitness:F2}/{t.WinRate:P0} wins; survival={t.SurvivorRate:P0}");
            }
            Console.WriteLine($"Saved profile: {output}");
            Console.WriteLine($"History: {csv}");
            Console.WriteLine($"Holdout report: {validationPath}");
            return 0;
        }
        catch(Exception ex) { Console.Error.WriteLine($"AI evolution failed: {ex.Message}"); return 1; }
    }

    private static void ValidateGenomeConfiguration()
    {
        float[] defaults=GenesFrom(AiBalanceProfile.Default);
        if(Min.Length!=GeneCount || Max.Length!=GeneCount ||
           BaseGenes.Length!=GeneCount || defaults.Length!=GeneCount)
            throw new InvalidOperationException(
                $"Genome shape mismatch: expected {GeneCount}, min={Min.Length}, max={Max.Length}, " +
                $"base={BaseGenes.Length}, profile={defaults.Length}.");

        for(int i=0;i<GeneCount;i++)
        {
            if(!float.IsFinite(Min[i]) || !float.IsFinite(Max[i]) ||
               Min[i]>Max[i] || BaseGenes[i]<Min[i] || BaseGenes[i]>Max[i])
                throw new InvalidOperationException(
                    $"Invalid gene bounds at index {i}: {Min[i]} <= {BaseGenes[i]} <= {Max[i]}.");

            if(MathF.Abs(defaults[i]-BaseGenes[i])>0.0001f)
                throw new InvalidOperationException(
                    $"Base gene {i} no longer matches AiBalanceProfile.Default ({BaseGenes[i]} vs {defaults[i]}).");
        }
    }

    private static List<float[]> Next(List<(float[] Genes,double Fitness)> rank,int count,Random r,float[] restartBase)
    {
        var next=new List<float[]>(count) { (float[])rank[0].Genes.Clone() };
        if(count>1) next.Add((float[])rank[1].Genes.Clone());
        int pool=Math.Clamp(count/2,2,rank.Count);
        while(next.Count<count)
        {
            if(r.NextDouble()<.06) { next.Add(Mutate(restartBase,r,.9,.45f)); continue; }
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
        p.TacticalFlankDistanceWeight,p.InvestigationDistancePenalty,p.VisibleCombatMediumScore,p.CommitDurationBaseSeconds,
        p.RangedEngagementRangeFraction,p.RangedRangeToleranceMinimum,p.RangedRangeToleranceFraction,
        p.MeleeEngagementRangeFraction,p.MeleeMinimumEngagementDistance,p.CoverSearchRadius,
        p.FlankNearBackoffDistance,p.FlankNearLateralDistance,p.FlankFarBackoffDistance,p.FlankFarLateralDistance,
        p.FlankNearRangeFraction,p.FlankFarRangeFraction,
        p.CoverThreatSeparationWeight,p.CoverTravelCostWeight,
        p.FlankBackoffProportion,p.FlankLateralProportion,p.FlankRangeErrorWeight,
        p.FlankTravelCostWeight,p.FlankLineOfSightBonus
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

    private static AiBalanceProfile Profile(float[] g) => _profileBase with
    {
        EngageScale=g[0], FlankScale=g[1], CoverScale=g[2], RetreatScale=g[3], ProneScale=g[4],
        SuppressiveFireScale=g[5], LastKnownScale=g[6], InvestigationScale=g[7], SquadContactScale=g[8],
        PatrolScale=g[9], ProneAdoptionMinimumUtility=g[10], FlankUtilityAdvantage=g[11],
        RegularSwitchMargin=g[12], CommittedSwitchMargin=g[13], MinimumInvestigationUtility=g[14],
        TacticalCoverPressureWeight=g[15], TacticalFlankDistanceStart=g[16], TacticalFlankDistanceWeight=g[17],
        InvestigationDistancePenalty=g[18], VisibleCombatMediumScore=g[19],
        CommitDurationBaseSeconds=g[20], RangedEngagementRangeFraction=g[21],
        RangedRangeToleranceMinimum=g[22], RangedRangeToleranceFraction=g[23],
        MeleeEngagementRangeFraction=g[24], MeleeMinimumEngagementDistance=g[25],
        CoverSearchRadius=g[26], FlankNearBackoffDistance=g[27],
        FlankNearLateralDistance=g[28], FlankFarBackoffDistance=g[29],
        FlankFarLateralDistance=g[30], FlankNearRangeFraction=g[31],
        FlankFarRangeFraction=g[32], CoverThreatSeparationWeight=g[33],
        CoverTravelCostWeight=g[34], FlankBackoffProportion=g[35],
        FlankLateralProportion=g[36], FlankRangeErrorWeight=g[37],
        FlankTravelCostWeight=g[38], FlankLineOfSightBonus=g[39]
    };

    private static Evaluation Evaluate(AiBalanceProfile p,int battles,int ticks,int team,int seed)
    {
        const int scenarioCount=4;
        double fitness=0, winRate=0, drawRate=0, survivorRate=0, ownHp=0, enemyHp=0, kills=0;
        var scenarioFitness=new double[scenarioCount];
        var scenarioWins=new double[scenarioCount];
        var scenarioDraws=new double[scenarioCount];
        var scenarioSurvivors=new double[scenarioCount];
        var scenarioOwnHp=new double[scenarioCount];
        var scenarioEnemyHp=new double[scenarioCount];
        var scenarioKills=new double[scenarioCount];
        var scenarioMatches=new int[scenarioCount];

        for(int i=0;i<battles;i++)
        {
            // Keep the layout for each match index identical across every candidate.
            int scenario=(i/2)%scenarioCount;
            int scenarioSeed=unchecked(seed+(i+1)*7919);
            BattleResult result=Battle(p,i%2==0,scenario,ticks,team,scenarioSeed);
            fitness+=result.Fitness;
            if(result.Outcome>0) winRate++;
            else if(result.Outcome==0) drawRate++;
            survivorRate+=result.SurvivorRate;
            ownHp+=result.OwnHealthRatio;
            enemyHp+=result.EnemyHealthRatio;
            kills+=result.EnemyKills;

            scenarioMatches[scenario]++;
            scenarioFitness[scenario]+=result.Fitness;
            if(result.Outcome>0) scenarioWins[scenario]++;
            else if(result.Outcome==0) scenarioDraws[scenario]++;
            scenarioSurvivors[scenario]+=result.SurvivorRate;
            scenarioOwnHp[scenario]+=result.OwnHealthRatio;
            scenarioEnemyHp[scenario]+=result.EnemyHealthRatio;
            scenarioKills[scenario]+=result.EnemyKills;
        }

        double divisor=Math.Max(1,battles);
        var scenarioResults=new ScenarioMetrics[scenarioCount];
        for(int i=0;i<scenarioCount;i++)
        {
            double n=Math.Max(1,scenarioMatches[i]);
            scenarioResults[i]=new ScenarioMetrics(
                ScenarioName(i),scenarioMatches[i],scenarioFitness[i]/n,
                scenarioWins[i]/n,scenarioDraws[i]/n,scenarioSurvivors[i]/n,
                scenarioOwnHp[i]/n,scenarioEnemyHp[i]/n,scenarioKills[i]/n);
        }

        return new Evaluation(fitness/divisor,winRate/divisor,drawRate/divisor,
            survivorRate/divisor,ownHp/divisor,enemyHp/divisor,kills/divisor,scenarioResults);
    }

    private static string ScenarioName(int index) => index switch
    {
        0 => "Open field",
        1 => "Barrier",
        2 => "Close start",
        3 => "Canyon",
        _ => "Unknown"
    };

    private static BattleResult Battle(AiBalanceProfile candidate,bool leftCandidate,int scenario,int ticks,int team,int scenarioSeed)
    {
        bool canyonScenario=scenario==3;
        var world=canyonScenario
            ? new WorldMap(regionsX:12,regionsY:6,layerCount:500)
            : new WorldMap(regionsX:8,regionsY:4,layerCount:32);
        if(canyonScenario)
            WorldGenerator.Generate(world,writeLog:false);
        else
            BuildFlatGround(world);
        if(scenario==1) Barrier(world,scenarioSeed);
        var sim=new UnitSimulation(unitCapacity:team*2+4,bodyCapacity:2048);
        sim.AI.SetBalanceProfileForFaction(1,leftCandidate?candidate:AiBalanceProfile.Default);
        sim.AI.SetBalanceProfileForFaction(2,leftCandidate?AiBalanceProfile.Default:candidate);
        List<UnitId> leftMembers=Spawn(sim,world,true,1,scenario,team,scenarioSeed);
        List<UnitId> rightMembers=Spawn(sim,world,false,2,scenario,team,scenarioSeed);
        sim.AI.SynchronizeSquadMemberships(new[]
        {
            new SquadMembershipSnapshot(1,leftMembers),
            new SquadMembershipSnapshot(2,rightMembers)
        });
        sim.AI.Enabled=true;
        Team left=ReadTeam(sim,leftMembers),right=ReadTeam(sim,rightMembers);
        for(int t=0;t<ticks;t++)
        {
            sim.Update(world,Dt);
            left=ReadTeam(sim,leftMembers);
            right=ReadTeam(sim,rightMembers);
            if(left.Alive==0||right.Alive==0) break;
        }
        Team own=leftCandidate?left:right, enemy=leftCandidate?right:left;
        int outcome=own.Alive>enemy.Alive?1:own.Alive<enemy.Alive?-1:
            own.Hp>enemy.Hp+.03?1:own.Hp+.03<enemy.Hp?-1:0;
        double outcomeScore=outcome>0?1d:outcome<0?0d:
            own.Hp>enemy.Hp+.03?.75:own.Hp+.03<enemy.Hp?.25:.5;
        double fitness=45*outcomeScore+20*own.Hp+20*(1-enemy.Hp)+
            15d*own.Alive/Math.Max(1,own.Total);
        return new BattleResult(fitness,outcome,
            own.Alive/(double)Math.Max(1,own.Total),own.Hp,enemy.Hp,enemy.Total-enemy.Alive);
    }

    private static List<UnitId> Spawn(
        UnitSimulation sim,WorldMap world,bool left,ushort faction,int scenario,int count,int scenarioSeed)
    {
        var members=new List<UnitId>(count);
        var positionRandom=new Random(unchecked(scenarioSeed^(left?0x45D9F3B:0x119DE1F3)));
        var equipmentRandom=new Random(scenarioSeed);
        int meleeIndex=equipmentRandom.Next(count);
        int rifleOffset=equipmentRandom.Next(3);

        for(int i=0;i<count;i++)
        {
            int margin=scenario==3?10:9;
            int spacing=(world.TileHeight-2*margin-1)/Math.Max(1,count-1);
            int jitter=Math.Min(2,spacing/4);
            int y=margin+i*(world.TileHeight-2*margin-1)/Math.Max(1,count-1)+
                positionRandom.Next(-jitter,jitter+1);
            int offset=(i%2)*2+positionRandom.Next(-1,2);
            int center=(world.TileWidth-1)/2;
            int x=scenario switch
            {
                1=>left?16+offset:world.TileWidth-17-offset,
                2=>left?center-31+offset:center+31-offset,
                3=>left?center-34+offset:center+34-offset,
                _=>left?center-50+offset:center+50-offset
            };
            x=Math.Clamp(x,2,world.MaxTileX-2);
            y=Math.Clamp(y,2,world.MaxTileY-2);
            float height=world.GetSurfaceHeight(x,y);
            UnitId id=sim.Spawn(UnitType.Colonist,new Vector3(x,y,height),
                bodyAngle:left?0f:MathF.PI,factionTag:faction);
            members.Add(id);

            if(i==meleeIndex)
            {
                var meleeWeapon=MeleeWeaponCatalog.CombatKnife;
                int meleeSlot=sim.AddInventoryItem(id,meleeWeapon);
                if(meleeSlot<0||!sim.EquipWeapon(id,meleeSlot,UnitWeaponSlot.Melee))
                    throw new InvalidOperationException($"Could not equip {meleeWeapon.Name}.");
            }
            else
            {
                var weapon=(i+rifleOffset)%3==0
                    ? WeaponCatalog.AssaultRifle
                    : WeaponCatalog.Pistol;
                int slot=sim.AddInventoryItem(id,weapon);
                if(slot<0||!sim.EquipWeapon(id,slot,UnitWeaponSlot.Primary))
                    throw new InvalidOperationException($"Could not equip {weapon.Name}.");
            }
        }
        return members;
    }

    private static void BuildFlatGround(WorldMap world)
    {
        // An empty WorldMap has no navigable surface. Use the same solid-column
        // representation as generated terrain so A*, LOS and bullets share a floor.
        for(int y=0;y<world.TileHeight;y++)
            for(int x=0;x<world.TileWidth;x++)
                world.SetSolidHeight(x,y,1,(ushort)1);
    }

    private static void Barrier(WorldMap world,int scenarioSeed)
    {
        int x=world.TileWidth/2;
        var random=new Random(unchecked(scenarioSeed^0x6D2B79F5));
        int firstGap=world.TileHeight/3+random.Next(-3,4);
        int secondGap=world.TileHeight*2/3+random.Next(-3,4);
        int firstRadius=3+random.Next(0,3);
        int secondRadius=3+random.Next(0,3);

        for(int y=4;y<world.TileHeight-4;y++)
        {
            if(Math.Abs(y-firstGap)<=firstRadius ||
               Math.Abs(y-secondGap)<=secondRadius)
                continue;

            // 2 m wall: physically present in LOS/projectile tracing and
            // separately forbidden to navigation. Ground exists beneath it.
            world.SetSolidHeight(x,y,(ushort)Math.Min(20,world.LayerCount),(ushort)1);
            world.SetNavigationBlocked(x,y,true);
        }
    }

    private static Team ReadTeam(UnitSimulation sim,IReadOnlyList<UnitId> roster)
    {
        int alive=0;
        double hp=0,max=0;
        foreach(UnitId id in roster)
        {
            int index=id.Index;
            float maximum=MathF.Max(0,sim.Health.OverallMaxHitPoints[index]);
            bool active=sim.Units.TryGetIndex(id,out _);
            float current=active
                ? Math.Clamp(sim.Health.OverallHitPoints[index],0,maximum)
                : 0f;
            if(active && current>0f) alive++;
            max+=maximum;
            hp+=current;
        }
        return new Team(roster.Count,alive,max>0?Math.Clamp(hp/max,0,1):0);
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
    private readonly record struct BattleResult(
        double Fitness,int Outcome,double SurvivorRate,double OwnHealthRatio,
        double EnemyHealthRatio,int EnemyKills);
    private readonly record struct ScenarioMetrics(
        string Name,int Matches,double AverageFitness,double WinRate,double DrawRate,
        double SurvivorRate,double OwnHealthRatio,double EnemyHealthRatio,double AverageEnemyKills);
    private readonly record struct Evaluation(
        double Fitness,double WinRate,double DrawRate,double SurvivorRate,
        double OwnHealthRatio,double EnemyHealthRatio,double AverageEnemyKills,
        ScenarioMetrics[] Scenarios);
}
