# TripleG3.P2P.Video

A small, DI-friendly RTP video surface used by TripleG3.Camera.Maui and tests.

Overview

- Public types live under the `TripleG3.P2P.Video` namespace.

- Key concepts: `RtpVideoSender`, `RtpVideoReceiver`, `EncodedAccessUnit`, and `ICipher`.

- Backwards compatibility: legacy RTP implementation remains under `TripleG3.P2P.Video.Rtp` and the new public types provide compatibility constructors when needed.

Usage

- Register in DI using service collection extensions (see `ServiceCollectionExtensions`).

- Create a receiver via DI and subscribe to `FrameReceived` to receive `EncodedAccessUnit` frames.

Notes

- The in-repo `XorTestCipher` is only for test purposes and is not cryptographically secure.

- Logging uses `Microsoft.Extensions.Logging`.

Contributing

- Keep public API surface stable; prefer new public types for external bindings.

