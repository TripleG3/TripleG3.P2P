# TripleG3.P2P Repository Instructions

- 2026-07-15: Target .NET 10 (`net10.0`) across the library and all test projects.
- 2026-07-15: Keep nullable reference types enabled and treat nullable warnings as errors in every project.
- 2026-07-15: The repository and NuGet package use the `GPL-3.0-only` license expression.
- 2026-07-15: Restore through the repository `NuGet.Config`; keep package versions centralized in `Directory.Packages.props`.
- 2026-07-15: Build and test `TripleG3.P2P.slnx` in Release before packing or publishing.
- 2026-07-15: Preserve existing wire protocols for compatibility; introduce versioned protocol values for incompatible wire-format improvements.
- 2026-07-15: Add focused regression tests for transport, serializer, and RTP behavior changes.
- 2026-07-15: Treat RTP video as experimental; use the canonical high-level types in `TripleG3.P2P.Video` and avoid obsolete high-level wrappers in `.Video.Rtp`.
- 2026-07-15: Use `SerializationProtocol.LengthPrefixed` for new attribute contracts; retain `None` only for existing wire compatibility.
- 2026-07-15: TCP accepted sockets are receive-only; outbound fan-out targets only configured endpoints and serializes writes per connection.
- 2026-07-15: Video receive loops start only through `StartAsync` and must be stopped or asynchronously disposed.
