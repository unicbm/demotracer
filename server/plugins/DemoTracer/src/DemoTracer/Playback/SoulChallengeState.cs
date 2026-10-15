/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

namespace DemoTracer;

internal enum SoulChallengePhase { Preparing, Fighting, Retrying, Cleared, Stopped }
internal enum SoulAttemptOutcome { Victory, Defeat, Interrupted }

internal sealed record SoulAttemptResult(
    int Number, SoulAttemptOutcome Outcome, double Seconds, int Kills, int Deaths, int Damage);

internal sealed class SoulChallengeState(string manifestPath, int sourceRound, int team)
{
    public string ManifestPath { get; } = manifestPath;
    public int SourceRound { get; } = sourceRound;
    public int Team { get; } = team;
    public SoulChallengePhase Phase { get; private set; } = SoulChallengePhase.Preparing;
    public bool Active => Phase is SoulChallengePhase.Preparing or SoulChallengePhase.Fighting or SoulChallengePhase.Retrying;
    public bool Fighting => Phase == SoulChallengePhase.Fighting;
    public List<SoulAttemptResult> Results { get; } = [];
    public int AttemptNumber => Results.Count + (Fighting ? 1 : 0);
    public int Kills { get; set; }
    public int Deaths { get; set; }
    public int Damage { get; set; }
    private double _startedAt;

    public bool BeginAttempt(double now)
    {
        if (Phase is not (SoulChallengePhase.Preparing or SoulChallengePhase.Retrying))
            return false;
        _startedAt = now;
        Kills = Deaths = Damage = 0;
        Phase = SoulChallengePhase.Fighting;
        return true;
    }

    public double Elapsed(double now) => Fighting ? Math.Max(0, now - _startedAt) : 0;

    public bool FinishRound(int winner, double now)
    {
        if (!Fighting || winner is not (2 or 3))
            return false;
        var won = winner == Team;
        Record(won ? SoulAttemptOutcome.Victory : SoulAttemptOutcome.Defeat, now);
        Phase = won ? SoulChallengePhase.Cleared : SoulChallengePhase.Retrying;
        return true;
    }

    public void Stop(double now)
    {
        if (!Active) return;
        if (Fighting) Record(SoulAttemptOutcome.Interrupted, now);
        Phase = SoulChallengePhase.Stopped;
    }

    private void Record(SoulAttemptOutcome outcome, double now)
        => Results.Add(new SoulAttemptResult(Results.Count + 1, outcome, Elapsed(now), Kills, Deaths, Damage));
}
