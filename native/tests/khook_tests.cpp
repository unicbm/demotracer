// Copyright (c) 2026 unicbm. All rights reserved.
// Licensed under the GNU Affero General Public License v3.0 only.
#include "../khook.h"
#include "../khook_signature.h"
#include <array>
#include <chrono>
#include <cstring>
#include <iostream>
#include <stdexcept>
#include <thread>

#if defined(_MSC_VER)
#define NOINLINE __declspec(noinline)
#else
#define NOINLINE __attribute__((noinline))
#endif

namespace
{
    void Require(bool condition, const char *message)
    {
        if (!condition) throw std::runtime_error(message);
    }
    using Fn = int64_t (*)(int64_t, int64_t, int64_t, int64_t, int64_t, int64_t);
    Fn original = nullptr;
    DemoTracerHooks::Hook<Fn> around;
    int calls = 0, peers = 0, posts = 0;
    bool scoped = false, block = false;
    bool scopeValid = true;
    bool peerOverride = false, peerBlock = false, expectScope = true;
    std::array<int64_t, 6> seen{};

    NOINLINE int64_t Target(int64_t a, int64_t b, int64_t c, int64_t d, int64_t e, int64_t f)
    {
        ++calls;
        seen = {a,b,c,d,e,f};
        return a + b * 2 + c * 3 + d * 4 + e * 5 + f * 6;
    }
    KHook::Return<int64_t> Around(int64_t a, int64_t b, int64_t c, int64_t d, int64_t e, int64_t f)
    {
        struct Scope { Scope() { scoped = true; } ~Scope() { scoped = false; } } scope;
        if (block) return {KHook::Action::Supersede, -7};
        return around.Continue(a + 10, b, c, d, e, f);
    }
    KHook::Return<int64_t> Peer(int64_t, int64_t, int64_t, int64_t, int64_t, int64_t)
    {
        ++peers;
        scopeValid &= !expectScope || block || scoped;
        if (peerBlock) return {KHook::Action::Supersede, -99};
        if (peerOverride) return {KHook::Action::Override, 9000};
        return {KHook::Action::Ignore};
    }
    KHook::Return<int64_t> Post(int64_t, int64_t, int64_t, int64_t, int64_t, int64_t)
    {
        ++posts;
        scopeValid &= !expectScope || block || scoped;
        return {KHook::Action::Ignore};
    }

    template <typename Predicate> void WaitFor(Predicate predicate)
    {
        const auto until = std::chrono::steady_clock::now() + std::chrono::seconds(2);
        while (!predicate())
        {
            Require(std::chrono::steady_clock::now() < until, "asynchronous hook registration timed out");
            std::this_thread::sleep_for(std::chrono::milliseconds(1));
        }
    }

    void FunctionChain()
    {
        auto invoke = static_cast<Fn>(&Target);
        std::vector<uint8_t> bytes(24);
        std::memcpy(bytes.data(), reinterpret_cast<void *>(invoke), bytes.size());
        const auto signature = DemoTracerHooks::Signature(bytes, std::vector<bool>(bytes.size(), false));
        KHook::Function<int64_t, int64_t, int64_t, int64_t, int64_t, int64_t, int64_t> peer(invoke, Peer, Post);
        Require(!around.Create(nullptr, Around, &original), "accepted null target");
        Require(around.Create(reinterpret_cast<void *>(invoke), Around, &original) && around.Enable(), "attach failed");
        Require(around.Active() && original, "hook did not expose explicit bypass");
        Require(!around.Enable(), "double registration accepted");
        WaitFor([&] { return invoke(1,2,3,4,5,6) == 101 && peers && posts; });
        calls = peers = posts = 0;
        scopeValid = true;
        Require(invoke(1,2,3,4,5000000000LL,6000000000LL) == 61000000040LL, "return or Win64 stack arguments changed");
        Require(calls == 1 && peers == 1 && posts == 1 && !scoped, "chain duplicated or failed to unwind");
        Require(scopeValid, "shared hook escaped temporary state");
        Require(seen == std::array<int64_t,6>{11,2,3,4,5000000000LL,6000000000LL}, "recall arguments changed");
        Require(DemoTracerHooks::FindSignature(reinterpret_cast<unsigned char *>(invoke), bytes.size(), bytes.size(), signature) == reinterpret_cast<void *>(invoke), "signature cannot see original hooked bytes");
        calls = 0;
        block = true;
        Require(invoke(1,2,3,4,5,6) == -7 && calls == 0 && !scoped, "suppression called original");
        block = false;
        calls = 0;
        peerOverride = true;
        Require(invoke(1,2,3,4,5,6) == 9000 && calls == 1, "recall lost another consumer's override");
        peerOverride = false;
        calls = 0;
        peerBlock = true;
        Require(invoke(1,2,3,4,5,6) == -99 && calls == 0, "recall ignored another consumer's suppression");
        peerBlock = false;
        // Raw calls deliberately bypass callbacks, for replay-owned weapon operations.
        calls = peers = posts = 0;
        Require(original(1,2,3,4,5,6) == 91 && calls == 1 && peers == 0 && posts == 0, "explicit bypass entered hooks");
        around.Remove();
        expectScope = false;
        calls = peers = posts = 0;
        Require(invoke(1,2,3,4,5,6) == 91 && calls == 1 && peers == 1 && posts == 1, "removing one consumer removed its peer");
        Require(!around.Create(reinterpret_cast<void *>(invoke), nullptr), "accepted missing callbacks");
        Require(around.Create(reinterpret_cast<void *>(invoke), Around) && around.Enable(),
                "callback-only hook required an unused bypass pointer");
        expectScope = true;
        WaitFor([&] { return invoke(1,2,3,4,5,6) == 101; });
        calls = peers = posts = 0;
        scopeValid = true;
        Require(invoke(1,2,3,4,5,6) == 101 && calls == 1 && peers == 1 && posts == 1,
                "callback-only hook changed the shared chain");
        Require(!original && scopeValid && !scoped, "callback-only hook leaked bypass or scope state");
        around.Remove();
        around.Remove();
        expectScope = false;
        Require(!around.Active() && invoke(1,2,3,4,5,6) == 91,
                "callback-only hook survived removal");
    }

    using VoidFn = void (*)(int *);
    VoidFn voidOriginal = nullptr;
    DemoTracerHooks::Hook<VoidFn> nested;
    int depth = 0, voidCalls = 0;
    NOINLINE void VoidTarget(int *value) { ++voidCalls; *value += 3; }
    KHook::Return<void> Nested(int *value)
    {
        ++depth;
        if (depth == 1) nested.Invoke(value);
        nested.Continue(value);
        --depth;
        return {KHook::Action::Ignore};
    }
    void RecursionAndRemoval()
    {
        Require(nested.Create(reinterpret_cast<void *>(&VoidTarget), Nested, &voidOriginal) && nested.Enable(), "void hook failed");
        int value = 0;
        nested.Invoke(&value);
        Require(value == 6 && voidCalls == 2 && depth == 0, "recursive callback lost state");
        nested.Remove();
        nested.Remove();
        Require(!nested.Active() && !voidOriginal, "removal kept active hook or bypass");
        VoidTarget(&value);
        Require(value == 9 && voidCalls == 3, "unload did not restore engine function");
    }

    struct Slot { int index; explicit Slot(int value = -1) : index(value) {} };
    struct Engine
    {
        virtual NOINLINE Slot Connect(const int &value) { return Slot(value + 1); }
    };
    struct Listener
    {
        int calls = 0;
        bool valid = true;
        KHook::Return<Slot> Post(Engine *, const int &value)
        {
            ++calls;
            valid &= !KHook::WasOriginalFunctionSkipped()
                && KHook::GetOriginalReturn<Slot>().index == value + 1;
            return {KHook::Action::Ignore, Slot()};
        }
    };
    NOINLINE Slot Connect(Engine *engine, const int &value) { return engine->Connect(value); }
    void VirtualLifecycle()
    {
        Engine engine, unrelated;
        Listener listener;
        auto hook = DemoTracerHooks::MakeVirtual(&Engine::Connect, &engine, &listener, nullptr, &Listener::Post);
        const int value = 41;
        Require(Connect(&engine, value).index == 42 && listener.calls == 1, "virtual post hook failed");
        Require(listener.valid, "virtual class return ABI changed");
        Require(Connect(&unrelated, value).index == 42 && listener.calls == 1, "per-instance hook affected another object");
        hook.reset();
        Require(Connect(&engine, value).index == 42 && listener.calls == 1, "virtual callback survived unload");
    }
}

int main()
{
    try
    {
        std::cerr << "Function chain\n";
        FunctionChain();
        around.Remove();
        Require(!original, "removed function retains bypass");
        std::cerr << "Recursion and removal\n";
        RecursionAndRemoval();
        std::cerr << "Virtual lifecycle\n";
        VirtualLifecycle();
        KHook::Shutdown();
        std::cout << "KHook chain, Win64 arguments, scopes, signatures, recursion and unload passed\n";
        return 0;
    }
    catch (const std::exception &e)
    {
        around.Remove();
        nested.Remove();
        KHook::Shutdown();
        std::cerr << e.what() << '\n';
        return 1;
    }
}
