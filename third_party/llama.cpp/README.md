# llama.cpp runtime (bundled)

The local inference engine the model host runs as its hidden child process (PROJECT_SPEC §5.6). The build copies
`win-x64` into the output as `llama.cpp\`, next to `Assistant.ModelHost.exe` and the app. Nothing is downloaded at build
time or run time.

| | |
|---|---|
| Release | llama.cpp v0.5.0, build `b11146` (commit `7fe450e19`), published 2026-09-23 |
| Package | `llama-b11146-bin-win-vulkan-x64.zip` (CPU backends and the Vulkan GPU backend) from https://github.com/ggml-org/llama.cpp/releases/tag/b11146 |
| Package SHA-256 | `55a378aa095b466979d85075234f66d7655c7a7483222af0c006c0e55b4d7bd6` (matches the digest GitHub publishes) |
| Licenses | llama.cpp and ggml: MIT (`LICENSE`); LLVM OpenMP runtime (`libomp.dll`): Apache 2.0 with LLVM exceptions (`LICENSE-LLVM-OpenMP`) |

## What is taken from the package

Only `llama-server.exe` and the native files it needs, found from its import tables:

- `llama-server.exe` is a small launcher; the server is `llama-server-impl.dll`, which loads `llama-common.dll`,
  `llama.dll`, `mtmd.dll` (images), `ggml.dll`, `ggml-base.dll` and `libomp.dll`.
- `ggml-cpu-*.dll` are the CPU backends. ggml loads the one that best fits the processor at run time, so they appear
  in no import table and all of them are kept.
- `ggml-vulkan.dll` is the GPU backend (Vulkan, so it serves NVIDIA, AMD and Intel cards alike). ggml loads it at run time like the CPU
  backends, so it appears in no import table. It needs `vulkan-1.dll` from the graphics driver; without a driver or a card, ggml
  skips it and the engine runs on the CPU. The host chooses the devices for each launch (`Processes/EngineDevices`).
  The 22 files beside it are byte for byte those of the earlier `win-cpu-x64` package of the same build.
- The Microsoft Visual C++ runtime (`MSVCP140.dll`, `VCRUNTIME140.dll`, `VCRUNTIME140_1.dll`) is not bundled. It comes
  from Windows' system folder, and the model host reports a missing one as a model-runtime status.

The other tools in the package (`llama-cli`, `llama-bench`, `llama-quantize`, the RPC backend and so on) are left out.

## Updating

Download the new `llama-<build>-bin-win-vulkan-x64.zip` from the official releases page, check its SHA-256 against the
digest GitHub shows, replace the files in `win-x64` with the same set, and update this page. The model host checks the
import tables when it starts (`Runtime/BundledRuntimeLocator`), so a build that needs a new DLL shows up as an
incomplete runtime. `Processes/LlamaServerOutput` reads the server's log lines, so check them against the new build's
output too.
