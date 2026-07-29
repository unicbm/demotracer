// Cross-platform inline hook wrapper over funchook + calling-convention macro

#pragma once

#include <funchook.h>

#if defined(_MSC_VER)
#  define BC_FASTCALL __fastcall
#else
#  define BC_FASTCALL
#endif

namespace BotController
{
    // One funchook_t per hook; mirrors MinHook create/enable/remove usage
    class Hook
    {
    public:
        Hook() = default;
        ~Hook() { Remove(); }
        Hook(const Hook &) = delete;
        Hook &operator=(const Hook &) = delete;

        // prepare: rewrites *orig to the trampoline entry. true on success
        bool Create(void *target, void *detour, void **orig)
        {
            if (m_fh || !target || !detour || !orig)
                return false;
            m_fh = funchook_create();
            if (!m_fh)
                return false;
            *orig = target; // funchook_prepare reads/writes this slot
            if (funchook_prepare(m_fh, orig, detour) != FUNCHOOK_ERROR_SUCCESS)
            {
                funchook_destroy(m_fh);
                m_fh = nullptr;
                return false;
            }
            return true;
        }

        // install the prepared hook
        bool Enable()
        {
            if (!m_fh || m_enabled)
                return false;
            if (funchook_install(m_fh, 0) != FUNCHOOK_ERROR_SUCCESS)
                return false;
            m_enabled = true;
            return true;
        }

        // uninstall + destroy; safe to call when inactive
        void Remove()
        {
            if (!m_fh)
                return;
            if (m_enabled)
            {
                funchook_uninstall(m_fh, 0);
                m_enabled = false;
            }
            funchook_destroy(m_fh);
            m_fh = nullptr;
        }

        bool Active() const { return m_enabled; }

    private:
        funchook_t *m_fh = nullptr;
        bool m_enabled = false;
    };
}
