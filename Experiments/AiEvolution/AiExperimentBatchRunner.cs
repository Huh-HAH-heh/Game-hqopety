using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RimClone.Experiments.AiEvolution;

/// <summary>
/// Runs independent simulation jobs with a fixed concurrency limit.
/// Results are returned in input order, not completion order. Each job must
/// use its own mutable WorldMap and UnitSimulation instances.
/// </summary>
public static class AiExperimentBatchRunner
{
    public static TResult[] Execute<TJob, TResult>(
        IReadOnlyList<TJob> jobs,
        int maxDegreeOfParallelism,
        Func<TJob, int, TResult> evaluate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(evaluate);
        if (maxDegreeOfParallelism < 1)
            throw new ArgumentOutOfRangeException(nameof(maxDegreeOfParallelism));
        if (jobs.Count == 0)
            return Array.Empty<TResult>();

        var results = new TResult[jobs.Count];
        int cancelled = 0;
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Min(maxDegreeOfParallelism, jobs.Count)
        };

        Parallel.For(0, jobs.Count, options, index =>
        {
            if (Volatile.Read(ref cancelled) != 0)
                return;
            if (cancellationToken.IsCancellationRequested)
            {
                Interlocked.Exchange(ref cancelled, 1);
                return;
            }

            try
            {
                results[index] = evaluate(jobs[index], index);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Exchange(ref cancelled, 1);
            }
        });

        if (Volatile.Read(ref cancelled) != 0 || cancellationToken.IsCancellationRequested)
            throw new OperationCanceledException("Experiment batch cancelled.", cancellationToken);
        return results;
    }
}
