# Microphone level

`IMicrophoneLevelMeter` measures how loud the default recording device is, for the voice-input glow
(PROJECT_SPEC §4.2). It is the only microphone access in the app.

- `Start(failed)` returns a session at once and opens the device on the session's own background thread, through
  WASAPI in shared mode, converted by the audio engine to 16 kHz mono 16-bit PCM. The COM interfaces are called
  through their vtables, as elsewhere in this module, so no built-in COM interop is needed.
- `IMicrophoneLevelSession.Level` is the RMS amplitude (0 to 1) of the most recent block of samples, about 10 ms of
  audio. It can be read from any thread and is 0 after the microphone closes. Disposing the session closes the
  microphone.
- Each block is reduced to its level inside the capture callback and handed straight back to the audio engine. No
  audio is recorded, copied, stored, transcribed or logged.
- `failed` is called at most once, on the capture thread, if the device cannot be opened or stops working before the
  session is disposed: `NoMicrophone`, `AccessDenied` (Windows privacy settings), `Disconnected` or `Unavailable`.
  Callers marshal it to their own thread.
- Logs (event IDs 5300–5303) carry only the failure kind, its HRESULT and how long the microphone was open.

The UI opens a session only while the user has voice input on, and closes it when voice input ends, the surface is
dismissed or loses focus (P2: no always-listening). Windows shows its microphone-in-use indicator meanwhile.

Verification: `dotnet test Assistant.sln`. Unit tests cover levels, background opening, closing, failures and log
content with a fake device. `dotnet test tests/Assistant.Windows.Tests -e ASSISTANT_TEST_MICROPHONE=1` also opens the
real default microphone for a moment and checks that samples arrive, or that it fails with a known reason.
