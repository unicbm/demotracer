# Shared native runtime support

BotController and BotHider compile these signature, schema, and KHook adapters
from their pinned `cs2-dtr-common` submodule. Product DLLs use Metamod's shared
KHook service; they do not link a private hook engine.

The tests are independent of both component repositories. Set `MMSOURCE_DEV`
to the recursively initialized Metamod source revision in
`../contracts/hook-runtime.v1.json`, then run from this repository's root:

```powershell
cmake -S native/tests -B .build/native-tests -A x64
cmake --build .build/native-tests --config Release
ctest --test-dir .build/native-tests -C Release --output-on-failure
```

These integration tests link the upstream standalone KHook engine only inside
the test build, covering hook lifetime, shared chains, and signature parsing.
They require CMake 3.28 or newer and a C++20 compiler. On Linux, omit `-A x64`.
