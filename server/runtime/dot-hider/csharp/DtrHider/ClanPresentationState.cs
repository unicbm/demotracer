/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using DtrHiderApi;

namespace DtrHider;

internal sealed class ClanPresentationState
{
    private bool _pending;

    internal bool Apply(BotHiderClan? requested, Func<BotHiderClan> read,
        Action<BotHiderClan> write, Action publish)
    {
        if (requested == null)
        {
            _pending = false;
            return false;
        }
        var current = read();
        var target = requested;
        var changed = current != target;
        if (changed || _pending)
        {
            _pending = true;
            if (changed)
                write(target);
            publish();
            if (read() != target)
                throw new InvalidOperationException("controller clan write was not retained");
            _pending = false;
        }
        return changed;
    }
}
