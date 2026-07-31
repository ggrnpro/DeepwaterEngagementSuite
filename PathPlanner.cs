using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using DeepwaterEngagementSuiteGGRN.PathPlannerData;
using ExileCore.Shared.Helpers;
using GameOffsets.Native;
using SixLabors.PolygonClipper;

namespace DeepwaterEngagementSuiteGGRN;

public class PathPlanner
{
    public record PerPointLootScore(Vector2i Point, double ScoreDiff, int NewRelics, int Loot);

    public record DetailedLootScore(List<PerPointLootScore> PerPointScore, double TotalScore, ExpeditionEnvironment Environment);

    private readonly Dictionary<object, double> _lootValueTable = new(ReferenceEqualityComparer.Instance);
    private readonly PlannerSettings _settings;
    private readonly int _validatedPoints;

    public PathPlanner(PlannerSettings settings)
    {
        _settings = settings;
        _validatedPoints = _settings.ValidatedIntermediatePoints + 1;
    }

    public double GetScore(List<Vector2i> state, ExpeditionEnvironment environment)
    {
        var lootList = environment.Loot
            .Where(x => environment.Bubbles.Any(b => b.Position.DistanceLessThanOrEqual(x.Item1.TruncateToVector2I(), b.Radius)))
            .Select(x => x.Item2)
            .ToHashSet();
        var score = 0.0;
        foreach (var lantern in state)
        {
            var localScore = 0.0;
            foreach (var (_, loot) in environment.Loot
                         .Where(x => x.Item1.DistanceLessThanOrEqual(lantern, environment.BubbleRadius))
                         .Where(x => lootList.Add(x.Item2)))
            {
                var (multiplier, sum) = (1, 0);
                localScore += _lootValueTable[loot] * multiplier * (1 + sum);
            }

            score += localScore;
        }

        return score;
    }

    //Sync with method above
    public DetailedLootScore GetDetailedScore(List<Vector2i> state, ExpeditionEnvironment environment)
    {
        var lootList = environment.Loot
            .Where(x => environment.Bubbles.Any(b => b.Position.DistanceLessThanOrEqual(x.Item1.TruncateToVector2I(), b.Radius)))
            .Select(x => x.Item2)
            .ToHashSet();
        var scorePerPoint = new List<PerPointLootScore>();
        var score = 0.0;
        foreach (var lantern in state)
        {
            var newRelics = 0;
            var newLoot = 0;

            var localScore = 0.0;
            foreach (var (_, loot) in environment.Loot
                         .Where(x => x.Item1.DistanceLessThanOrEqual(lantern, environment.BubbleRadius))
                         .Where(x => lootList.Add(x.Item2)))
            {
                newLoot++;
                var (multiplier, sum) = (1,0);
                localScore += _lootValueTable[loot] * multiplier * (1 + sum);
            }

            scorePerPoint.Add(new PerPointLootScore(lantern, localScore, newRelics, newLoot));
            score += localScore;
        }

        return new DetailedLootScore(scorePerPoint, score, environment);
    }

    public void Init(ExpeditionEnvironment environment)
    {
        _lootValueTable.Clear();
        foreach (var (_, loot) in environment.Loot)
        {
            _lootValueTable[loot] = loot switch
            {
                Chest { Type: var type } => _settings.ChestSettingsMap.GetValueOrDefault(type, new ChestSettings()).Weight,
                _ => 0,
            };
        }

        _lootValueTable.TrimExcess();
    }

    public IEnumerable<PathState> GetBestPathSeries(ExpeditionEnvironment environment)
    {
        if (environment.MaxBubbles <= 0)
        {
            yield return new PathState(new List<Vector2i>(), 0);
            yield break;
        }

        var bestPath = Enumerable.Repeat(Vector2i.Zero, environment.MaxBubbles).ToList();
        var batch = Enumerable.Range(0, _settings.PathGenerationSize * 2).Select(_ => BuildPath(environment)).ToList();
        while (true)
        {
            var batchWithValues = batch
                .Select(x => (GetScore(x, environment), x))
                .OrderByDescending(x => x.Item1)
                .Take(_settings.PathGenerationSize)
                .ToList();
            var newPaths = Enumerable.Range(0, (int)(_settings.PathGenerationSize * _settings.NewRandomPathInjectionRate)).Select(_ => BuildPath(environment));
            var newBatch = (newPaths).Append(bestPath).ToList();
            if (batchWithValues[0].Item1 > GetScore(bestPath, environment) ||
                bestPath.All(x => x.Equals(Vector2i.Zero)))
            {
                bestPath = batchWithValues[0].x;
            }

            yield return new PathState(bestPath, GetScore(bestPath, environment));
            batch = newBatch;
        }
    }

    private List<Vector2i> BuildPath(ExpeditionEnvironment environment)
    {
        var polygon = environment.Bubbles.Select(x => DeepwaterEngagementSuiteGGRN.GetCirclePolygon(x.Position, x.Radius)).Aggregate(PolygonClipper.Union);
        var count = environment.MaxBubbles;
        var points = new List<Vector2i>();
        while (count>0)
        {
            count--;
            var candidatePoints = polygon.SelectMany(p => p).ToList();
            var point = new Vertex(0, 0);
            for (int i = 0; i < 100; i++)
            {
                point = candidatePoints[Random.Shared.Next(candidatePoints.Count)];
                if (environment.IsValidPlacement(new Vector2((float)point.X, (float)point.Y)))
                {
                    break;
                }
            }
            polygon = PolygonClipper.Union(polygon,(DeepwaterEngagementSuiteGGRN.GetCirclePolygon(new Vector2((int)point.X, (int)point.Y), environment.BubbleRadius)));
            points.Add(new Vector2i((int)point.X,(int)point.Y));
        }

        return points;
    }
}