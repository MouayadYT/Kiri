# Voice: runtime and model notices (step 125)

The Assistant's voice (speech recognition, spoken answers, the "Kiri" wake word) runs locally on the processor. **No voice binary or
model file is in this repository.** The runtime comes from a NuGet package and the models are files the user (or a package) puts in
`%LOCALAPPDATA%\Assistant\voices\<group id>\` or in `<install folder>\assets\voices\<group id>\` (PROJECT_SPEC §3.5, §5.11). This file
records what they are and the terms each states, so that a package that ships them can carry the right notices. It is not legal advice:
read the license file or model card that comes with each download before redistributing it.

## Runtime

| Part | What | Terms stated by its project |
|---|---|---|
| sherpa-onnx 1.13.8 (NuGet `org.k2fsa.sherpa.onnx`, managed API plus `sherpa-onnx-c-api.dll`) | Runs all the models below (offline text-to-speech, streaming recognition, keyword spotting) | Apache License 2.0 |
| ONNX Runtime (`onnxruntime.dll`, shipped inside that package) | Runs the ONNX models, on the processor only | MIT |
| espeak-ng (compiled into sherpa-onnx; its `espeak-ng-data` folder ships with the Kitten, Kokoro and Piper voices) | Turns text into phonemes | **GPL-3.0-or-later.** This is the notice to take most care over before distributing an installer that contains it |

## Voice and speech files (group ids)

| Group id | Files | Source | Terms stated with it |
|---|---|---|---|
| `kitten-tts-mini` | KittenTTS Mini 0.8 (80M), sherpa-onnx conversion `kitten-mini-en-v0_8` | KittenML | Apache License 2.0 (its `LICENSE`) |
| `kokoro-82m-onnx` | Kokoro-82M, int8, sherpa-onnx conversion `kokoro-int8-multi-lang-v1_0` | hexgrad / k2-fsa | Apache License 2.0 (its `LICENSE`) |
| `piper` | Piper voice `en_US-lessac-medium` (`vits-piper-en_US-lessac-medium`) | rhasspy / Piper | Its model card points to the Blizzard 2013 Lessac dataset's own license page (cstr.ed.ac.uk): **read it for what it allows before redistributing the voice** |
| `speech-recognition` | NVIDIA NeMo streaming FastConformer CTC (English, 480 ms, int8), sherpa-onnx conversion | NVIDIA / k2-fsa | CC-BY-4.0 (attribution required) |
| `wake-word` | Keyword spotter `sherpa-onnx-kws-zipformer-gigaspeech-3.3M-2024-01-01` (int8 files only) | k2-fsa | Apache License 2.0 (its README); it was trained on GigaSpeech |

## What the app does with them

- The files are read from disk and used in memory only. Nothing is uploaded and the app never downloads them.
- A user's own folder is used before a packaged one and is not checked against a manifest; a packaged group is verified against its
  `manifest.json` (size and SHA-256) before it is loaded.
- The wake word's keyword list (the spelling of "Kiri") is written under `%LOCALAPPDATA%\Assistant\cache\` when the listener starts.
