/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

namespace DemoTracer;

internal static class ReplayPlanOverridePolicy
{
    internal static bool DeferExistingReplayCleanupUntilRoundStart(bool restartRequested)
        => restartRequested;
}

internal sealed class ReplayPlanState
{
    public bool Armed { get; set; }
    public bool ArmedLoop { get; set; }
    public string ArmedLabel { get; set; } = string.Empty;
    public string ArmedManifestPath { get; set; } = string.Empty;
    public int ArmedSourceRound { get; set; } = -1;
    public bool ArmedPrepared { get; set; }
    public int ArmedPreparePollToken { get; set; }

    public bool SequenceActive { get; set; }
    public string SequenceManifestPath { get; set; } = string.Empty;
    public int[] SequenceRounds { get; set; } = [];
    public int SequenceIndex { get; set; }
    public bool SequencePrepared { get; set; }
    public int SequencePreparedRound { get; set; } = -1;
    public int SequencePreparePollToken { get; set; }

    public bool PlayoffPreparePending { get; set; }
    public bool PlayoffPendingCanLoad { get; set; }
    public int PlayoffPrepareToken { get; set; }
    public int PlayoffPendingTRound { get; set; } = -1;
    public int PlayoffPendingCtRound { get; set; } = -1;
    public string PlayoffPendingReason { get; set; } = string.Empty;
    public string PlayoffPendingPrepareReason { get; set; } = string.Empty;
    public bool PlayoffPrepared { get; set; }
    public int PlayoffPreparedTRound { get; set; } = -1;
    public int PlayoffPreparedCtRound { get; set; } = -1;
    public string PlayoffPreparedLabel { get; set; } = string.Empty;
    public int PlayoffRoundIndex { get; set; }

    public void ClearArmed()
    {
        Armed = false;
        ArmedLoop = false;
        ArmedLabel = string.Empty;
        ArmedManifestPath = string.Empty;
        ArmedSourceRound = -1;
        ArmedPrepared = false;
        ArmedPreparePollToken++;
    }

    public void ClearArmedPreparation()
        => ArmedPrepared = false;

    public void ClearSequence()
    {
        SequenceActive = false;
        SequenceManifestPath = string.Empty;
        SequenceRounds = [];
        SequenceIndex = 0;
        SequencePrepared = false;
        SequencePreparedRound = -1;
        SequencePreparePollToken++;
    }

    public void ClearSequencePreparation()
    {
        SequencePrepared = false;
        SequencePreparedRound = -1;
    }

    public bool ClearPlayoffPending()
    {
        var wasPending = PlayoffPreparePending;
        PlayoffPreparePending = false;
        PlayoffPendingCanLoad = false;
        PlayoffPrepareToken++;
        PlayoffPendingTRound = -1;
        PlayoffPendingCtRound = -1;
        PlayoffPendingReason = string.Empty;
        PlayoffPendingPrepareReason = string.Empty;
        return wasPending;
    }

    public bool ClearPlayoffPrepared(bool resetRoundIndex)
    {
        var wasPrepared = PlayoffPrepared;
        PlayoffPrepared = false;
        PlayoffPreparedTRound = -1;
        PlayoffPreparedCtRound = -1;
        PlayoffPreparedLabel = string.Empty;
        if (resetRoundIndex)
            PlayoffRoundIndex = 0;
        return wasPrepared;
    }
}
