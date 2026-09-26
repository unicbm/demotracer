/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using DemoTracerBotHiderApi;

namespace BotHiderImpl;

// Owned by one controller incarnation. Keep the original pair across replacements
// and failed notifications so release can restore it without capturing an override.
internal sealed class ClanPresentationState
{
    private BotHiderClan? _base;
    private bool _pending;

    internal bool HasOverride => _base != null;

    internal bool Apply(BotHiderClan? requested, Func<BotHiderClan> read,
        Action<BotHiderClan> write, Action publish)
    {
        if (requested == null && _base == null)
            return false;
        var current = read();
        if (requested != null)
            _base ??= current;
        var target = requested ?? _base!;
        var changed = current != target;
        if (changed || _pending)
        {
            _pending = true;
            write(target);
            publish();
            if (read() != target)
                throw new InvalidOperationException("controller clan write was not retained");
            _pending = false;
        }
        if (requested == null)
            _base = null;
        return changed;
    }
}
