// Copyright (c) 2026 unicbm. All rights reserved.
// Licensed under the GNU Affero General Public License v3.0 only.
#pragma once

#include <khook.hpp>
#include <memory>
#include <stdexcept>
#include <utility>

namespace DemoTracerHooks
{
    // Uses the single KHook interface supplied by Metamod. No private engine.
    template <typename Function> class Hook;

    template <typename R, typename... Args>
    class Hook<R (*)(Args...)>
    {
        using Function = R (*)(Args...);
        using Callback = KHook::Return<R> (*)(Args...);
        class CheckedHook : public KHook::Function<R, Args...>
        {
        public:
            using KHook::Function<R, Args...>::Function;
            bool Active() const { return this->_associated_hook_id != KHook::INVALID_HOOK; }
        };

    public:
        Hook() = default;
        Hook(const Hook &) = delete;
        Hook &operator=(const Hook &) = delete;
        ~Hook() { Remove(); }

        bool Create(void *target, Callback pre, Function *original = nullptr, Callback post = nullptr)
        {
            if (m_target || !target || (!pre && !post))
                return false;
            m_target = reinterpret_cast<Function>(target);
            m_pre = pre;
            m_post = post;
            m_original = original;
            if (original)
                *original = nullptr;
            return true;
        }

        bool Enable()
        {
            if (!m_target || m_hook)
                return false;
            auto hook = std::make_unique<CheckedHook>(m_target, m_pre, m_post);
            if (!hook->Active())
                return false;
            // Only explicit raw engine calls outside callbacks use this bypass.
            if (m_original)
                *m_original = reinterpret_cast<Function>(KHook::FindOriginal(reinterpret_cast<void *>(m_target)));
            m_hook = std::move(hook);
            return true;
        }

        void Remove()
        {
            // KHook destruction synchronously drains callbacks before freeing them.
            m_hook.reset();
            if (m_original)
                *m_original = nullptr;
            m_original = nullptr;
            m_target = nullptr;
            m_pre = m_post = nullptr;
        }

        bool Active() const { return m_hook && m_hook->Active(); }

        // Explicit engine invocation, including every registered callback.
        R Invoke(Args... args) { return m_target(std::forward<Args>(args)...); }

        // Resume the shared chain with these arguments. Recall runs remaining
        // callbacks and the original once, preserving peer overrides. Local
        // scopes stay alive until the chain returns.
        KHook::Return<R> Continue(Args... args)
        {
            return KHook::Recall(m_target, KHook::Return<R>{KHook::Action::Ignore},
                                 std::forward<Args>(args)...);
        }

    private:
        Function m_target = nullptr;
        Function *m_original = nullptr;
        Callback m_pre = nullptr;
        Callback m_post = nullptr;
        std::unique_ptr<CheckedHook> m_hook;
    };

    template <typename C, typename R, typename... Args>
    class Virtual : public KHook::Virtual<C, R, Args...>
    {
    public:
        using KHook::Virtual<C, R, Args...>::Virtual;
        bool Registered() const { return !this->_addr_hook_ids.empty(); }
    };

    template <typename C, typename R, typename... Args, typename Context, typename Pre, typename Post>
    std::unique_ptr<KHook::__Hook> MakeVirtual(R (C::*method)(Args...), C *instance,
                                             Context *context, Pre pre, Post post)
    {
        auto hook = std::make_unique<Virtual<C, R, Args...>>(method, context, pre, post);
        hook->Add(instance);
        if (!hook->Registered())
            throw std::runtime_error("KHook virtual registration failed");
        return hook;
    }
}
