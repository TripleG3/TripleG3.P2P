# TripleG3.P2P Repository Review

Review date: 2026-07-15  
Scope: `src/TripleG3.P2P`, both test projects, package metadata, documentation, and the release workflow.  
Method: read-only source review plus local build/test execution. No production or test source was changed.

## Executive Summary

The core abstractions are compact, the repository builds cleanly, and all 36 existing tests pass. The strongest parts are the small `ISerialBus` API, explicit UDP/RTP headers, serializer abstraction, UDP fan-out tests, and the attempt to make video buffer ownership explicit.

The current green test suite does not cover several important paths. The highest-priority concerns are:

1. The repository says MIT while the included license is GPL-3.0, and the NuGet package has no explicit license metadata.
2. The configuration/DI video path is not usable as registered and its internal depacketizer emits buffers other than the buffers it filled.
3. The advertised cipher paths either ignore the cipher or protect different byte ranges for fragmented H.264 packets.
4. TCP connection tracking can duplicate messages, disclose outbound messages to any accepted client, and prevent reconnection after a disconnect.
5. The `None` serializer is not round-trip safe for delimiters, empty strings, arbitrary nesting, reordered attributes, culture-sensitive values, or `DateTimeOffset`.
6. The release workflow publishes without running either test project.

The video feature is described as both experimental and a stable 1.x surface. Until the P0/P1 video findings are addressed, it should remain explicitly experimental and should not be treated as production-ready.

## Severity Guide

| Priority | Meaning |
| --- | --- |
| P0 | Resolve before the next package release or before promoting the affected API. |
| P1 | High likelihood of data loss, corruption, security surprise, or a persistent runtime failure. |
| P2 | Robustness, API consistency, observability, or maintainability issue. |
| P3 | Optional improvement with lower immediate risk. |

## Findings

### P0-1: License and package metadata contradict each other

Evidence:

- [README.md](README.md#L491-L493) declares MIT.
- [LICENSE](LICENSE) contains the GNU General Public License version 3.
- [TripleG3.P2P.csproj](src/TripleG3.P2P/TripleG3.P2P.csproj#L29-L38) packs the license file but declares neither `PackageLicenseExpression` nor `PackageLicenseFile`.

Impact:

Consumers cannot reliably determine the package's license, and the README currently communicates materially different redistribution obligations from the bundled license. This is a release and legal-compliance blocker.

Recommendation:

Decide the intended license, make `README.md` and `LICENSE` agree, and add matching NuGet metadata. For GPL-3.0-only, use `PackageLicenseExpression`; for a custom or file-based license, use `PackageLicenseFile`. Verify the generated `.nuspec` before publishing.

### P0-2: The video DI registration cannot provide a usable sender

Evidence:

- [ServiceCollectionExtensions.cs](src/TripleG3.P2P/Video/ServiceCollectionExtensions.cs#L13-L18) registers a `RtpVideoSender` factory for `IRtpVideoSender`.
- [RtpVideoSender.cs](src/TripleG3.P2P/Video/RtpVideoSender.cs#L11-L14) implements only `IDisposable`, not `Video.Abstractions.IRtpVideoSender`.
- The same registration constructs default sender/receiver configs. [RtpVideoSenderConfig.cs](src/TripleG3.P2P/Video/Primitives/RtpVideoSenderConfig.cs#L3-L11) defaults the remote port to `0`; [RtpVideoReceiverConfig.cs](src/TripleG3.P2P/Video/Primitives/RtpVideoReceiverConfig.cs#L3-L10) defaults the local port to `0`.
- [VideoP2POptions.cs](src/TripleG3.P2P/Video/VideoP2POptions.cs#L5-L9) exposes lifetimes only, so callers cannot supply endpoints through the extension.

Impact:

Resolving `IRtpVideoSender` through `GetRequiredService<IRtpVideoSender>()` requires a cast to an interface the object does not implement. Even after fixing that type mismatch, the registered endpoints do not identify a peer: the receiver binds an arbitrary ephemeral port and the sender targets port zero.

Recommendation:

Make `RtpVideoSender` implement the abstraction, require validated sender/receiver configuration in DI, and fail fast for invalid ports, MTU, payload type, or missing endpoints. Add a test that registers the extension, resolves both interfaces, starts them, and transfers a frame.

### P0-3: The configuration-based depacketizer emits uninitialized or corrupted access units

Evidence:

- In the single-NAL path, [Depacketizer.cs](src/TripleG3.P2P/Video/Internal/Depacketizer.cs#L76-L83) rents and fills `buf`, then constructs the access unit around a different, newly allocated `ArrayPoolFrame`.
- In the FU-A path, [Depacketizer.cs](src/TripleG3.P2P/Video/Internal/Depacketizer.cs#L89-L103) fills `outBuf`, then again constructs the access unit around a different `ArrayPoolFrame`.
- Fragment arrays are rented by requested payload size, but assembly totals and copies each array's full pool capacity via `b.Length`, not the number of bytes written.
- This internal depacketizer is the path used by the configuration/DI receiver when `_legacyImpl` is null in [RtpVideoReceiver.cs](src/TripleG3.P2P/Video/RtpVideoReceiver.cs#L73-L81).

Impact:

The emitted `AnnexB` memory does not contain the bytes copied into `buf` or `outBuf`. Fragmented frames can additionally include unwritten pool capacity. The existing unit test checks only that an access unit is non-null, so this corruption remains green.

Recommendation:

Use a single owner for each completed frame: write directly into an `ArrayPoolFrame`, or add an owner that wraps the already-rented array. Store `(buffer, writtenLength)` for every fragment. Add byte-for-byte tests for single NAL, FU-A, multiple NALs, loss, and sequence wrap through the public configuration-based receiver.

### P1-1: Cipher behavior is inconsistent and can silently send plaintext

Evidence:

- The configuration sender stores `_cipher` in [RtpVideoSender.cs](src/TripleG3.P2P/Video/RtpVideoSender.cs#L14-L36), but its send path packetizes and transmits without using that field.
- The legacy FU-A packetizer encrypts only fragment data after the two FU headers in [H264RtpPacketizer.cs](src/TripleG3.P2P/Video/Rtp/H264RtpPacketizer.cs#L56-L75).
- The matching depacketizer decrypts the entire RTP payload, including the FU indicator and FU header, in [H264RtpDepacketizer.cs](src/TripleG3.P2P/Video/Rtp/H264RtpDepacketizer.cs#L18-L25).
- [CipherAdapter.cs](src/TripleG3.P2P/Video/CipherAdapter.cs#L5-L20) supplies only a payload-sized output span even though [IVideoPayloadCipher.cs](src/TripleG3.P2P/Video/IVideoPayloadCipher.cs#L6-L16) advertises expansion through `OverheadBytes` and a returned length.

Impact:

The public configuration constructor accepts a cipher but sends plaintext. The compatibility path works only for a no-op or specially tolerant cipher: a normal transform also changes the plaintext FU headers during decryption, so fragmented frames no longer parse. Authenticated encryption that appends a tag cannot fit in the supplied buffer.

Recommendation:

Define one precise protected region and one ownership/length contract. Account for authentication-tag overhead in MTU calculations and packet lengths, reject unsupported expanding ciphers, and never accept a cipher that is not used. Add end-to-end tests with a non-no-op cipher for single-NAL and FU-A frames, including an implementation with nonzero overhead and tamper rejection.

### P1-2: `RtpVideoReceiver` starts an orphaned receive loop

Evidence:

- The constructor starts a receive loop immediately in [RtpVideoReceiver.cs](src/TripleG3.P2P/Video/RtpVideoReceiver.cs#L26-L33).
- `StartAsync` replaces `_cts` and starts another loop in [RtpVideoReceiver.cs](src/TripleG3.P2P/Video/RtpVideoReceiver.cs#L52-L58).
- `StopAsync` cancels only the replacement token in [RtpVideoReceiver.cs](src/TripleG3.P2P/Video/RtpVideoReceiver.cs#L87-L91).
- The original task is never awaited, and `Dispose` neither waits for shutdown nor disposes the token source.

Impact:

The expected `construct -> StartAsync -> StopAsync` lifecycle leaves the constructor-started loop active. Two receives can compete for datagrams, shutdown is nondeterministic, and restart/disposal races are hidden by broad exception swallowing.

Recommendation:

Do not perform asynchronous startup in the constructor. Make `StartAsync` idempotent, retain exactly one task and token source, cancel and await the task in `StopAsync`, and implement `IAsyncDisposable` if shutdown can block. Add start/stop/restart and dispose-during-receive tests.

### P1-3: RTP reordering never recovers from a missing packet

Evidence:

- [RtpReorderBuffer.cs](src/TripleG3.P2P/Video/Rtp/RtpReorderBuffer.cs#L9-L29) waits indefinitely for `_expected`.
- When capacity is exceeded it removes a stored future packet, but it never advances the missing expected sequence number.
- The capacity eviction uses natural `ushort` ordering, which is not RTP circular ordering around `65535 -> 0`.
- Loss accounting in [RtpVideoReceiver.cs](src/TripleG3.P2P/Video/Rtp/RtpVideoReceiver.cs#L48-L64) casts the sequence delta to `ushort`; the subsequent `diff < 0` branch is unreachable. A late packet can therefore add roughly 65,000 phantom losses.

Impact:

One lost packet can permanently block every later frame on that receiver. Reordering also corrupts loss statistics, and sequence wrap can evict the wrong packet.

Recommendation:

Use RFC 3550 extended-sequence tracking and a bounded time/window policy. Once the reorder deadline or window is exceeded, declare the missing packet lost, advance the cursor, and release later packets. Test loss followed by a complete later frame, duplicates, late packets, bursts, and wraparound.

### P1-4: TCP conflates inbound connections with configured outbound recipients

Evidence:

- The listener binds `IPAddress.Any`, accepts every connection, and adds each accepted client to `_clients` in [TcpSerialBus.cs](src/TripleG3.P2P/Tcp/TcpSerialBus.cs#L35-L59).
- Outbound clients are added to the same dictionary in [TcpSerialBus.cs](src/TripleG3.P2P/Tcp/TcpSerialBus.cs#L64-L88).
- Accepted clients are keyed by their ephemeral source endpoint, while outbound clients are keyed by the peer's listening endpoint.
- Every send writes to every entry in [TcpSerialBus.cs](src/TripleG3.P2P/Tcp/TcpSerialBus.cs#L203-L216).

Impact:

If A sends to B and B then replies, B can hold both the accepted A-to-B socket and a new B-to-A socket. Because TCP is full duplex, B writes the reply to both and A receives it twice. More seriously, any client that can connect to the listener is automatically included in future outbound fan-out, even if it was not configured as a recipient. There is no connection limit, authentication, or allowlist.

Recommendation:

Separate accepted receive sessions from the configured destination set. Define one connection owner per peer identity, canonicalize peer identity independently of ephemeral ports, and authenticate or explicitly authorize accepted peers before sending application data to them. Add exact-once bidirectional tests and an unconfigured-client rejection test.

### P1-5: TCP disconnect cleanup prevents reliable reconnection, and writes are unsynchronized

Evidence:

- The receive-loop cleanup closes the client before reading `RemoteEndPoint` in [TcpSerialBus.cs](src/TripleG3.P2P/Tcp/TcpSerialBus.cs#L112-L118). After close, that endpoint is unavailable; the catch hides the failed removal.
- [TcpSerialBus.cs](src/TripleG3.P2P/Tcp/TcpSerialBus.cs#L69-L79) skips connection attempts whenever the stale key remains present.
- Send failures in [TcpSerialBus.cs](src/TripleG3.P2P/Tcp/TcpSerialBus.cs#L205-L215) also leave failed clients in the dictionary.
- Concurrent `SendAsync` calls can write separate frames to the same `NetworkStream` without a per-connection send lock or queue.

Impact:

After an abrupt peer restart, the bus can permanently believe the old connection still exists and never reconnect. Concurrent frame writes can interleave or reorder at the application framing layer, causing the receiver to interpret payload bytes as a length and close the connection.

Recommendation:

Capture the dictionary key before close, remove with key/value matching, and remove failed clients from every send failure path. Represent each connection with a lifecycle object containing its stream, cancellation, receive task, and a single-writer queue or semaphore. Add abrupt-disconnect, reconnect, concurrent-send, and close/restart tests.

### P1-6: `SendAsync` reports success after connection, send, or cancellation failures

Evidence:

- TCP connection failures are swallowed in [TcpSerialBus.cs](src/TripleG3.P2P/Tcp/TcpSerialBus.cs#L69-L82), and per-client write failures are swallowed in [TcpSerialBus.cs](src/TripleG3.P2P/Tcp/TcpSerialBus.cs#L203-L215).
- UDP catches every send failure in [UdpSerialBus.cs](src/TripleG3.P2P/Udp/UdpSerialBus.cs#L156-L167).
- UDP does not pass the supplied cancellation token to its socket send; TCP passes it but catches the resulting cancellation.
- Both receive paths and subscriber invocation also suppress exceptions without an `ILogger` or diagnostic event.

Impact:

Callers cannot distinguish full delivery, partial broadcast delivery, zero connected recipients, cancellation, serialization fallback, or a socket failure. This is especially surprising for the transport documented as reliable.

Recommendation:

Always propagate `OperationCanceledException`. Define broadcast semantics explicitly: either throw when no destination succeeds, return a per-endpoint result, or expose a delivery-failure event while preserving the current interface. Integrate structured `ILogger<T>` diagnostics and remove dead clients on failure.

### P1-7: The `None` wire format is not self-delimiting

Evidence:

- [NoneMessageSerializer.cs](src/TripleG3.P2P/Serialization/NoneMessageSerializer.cs#L17-L17) uses the raw byte delimiter `@-@`.
- Property values are concatenated without escaping or lengths in [NoneMessageSerializer.cs](src/TripleG3.P2P/Serialization/NoneMessageSerializer.cs#L55-L84).
- Deserialization splits only by constructor parameter count in [NoneMessageSerializer.cs](src/TripleG3.P2P/Serialization/NoneMessageSerializer.cs#L132-L160) and [NoneMessageSerializer.cs](src/TripleG3.P2P/Serialization/NoneMessageSerializer.cs#L174-L194).

Impact:

`Chat("a@-@b", "c")` round-trips as different values. Nested objects are only unambiguous in limited positions because nested and outer properties share the same delimiter. Multiple nested objects or a nested object before another outer property cannot be decoded reliably. Null and empty string are also conflated.

Recommendation:

Introduce a versioned, length-prefixed binary format rather than changing the existing protocol in place. Reserve a new `SerializationProtocol` value, encode UTF-8 byte lengths, and preserve explicit null markers. Keep `None` only for backward compatibility and document its constraints.

### P1-8: `None` deserialization does not honor the advertised contract metadata

Evidence:

- Serialization orders properties by `[Udp(Order)]` in [NoneMessageSerializer.cs](src/TripleG3.P2P/Serialization/NoneMessageSerializer.cs#L45-L51).
- Deserialization ignores those properties and invokes the public constructor with the most parameters in [NoneMessageSerializer.cs](src/TripleG3.P2P/Serialization/NoneMessageSerializer.cs#L132-L164).
- Empty slices become `null` for reference types in [NoneMessageSerializer.cs](src/TripleG3.P2P/Serialization/NoneMessageSerializer.cs#L150-L157).
- Primitive serialization uses culture-sensitive `ToString()`, while conversion uses current-culture `Convert.ChangeType` in [NoneMessageSerializer.cs](src/TripleG3.P2P/Serialization/NoneMessageSerializer.cs#L53-L65) and [NoneMessageSerializer.cs](src/TripleG3.P2P/Serialization/NoneMessageSerializer.cs#L165-L172).
- `DateTimeOffset` is advertised as supported in [README.md](README.md#L351-L353), but `Convert.ChangeType(string, typeof(DateTimeOffset))` throws `InvalidCastException`.

Impact:

Attribute order can disagree with constructor order, overloaded constructors can select the wrong contract, empty strings become null, and values can change or fail between peers with different cultures. This contradicts the documented deterministic wire contract.

Recommendation:

Build and validate one cached contract model per type. Map ordered annotated members to constructor parameters by an explicit rule, reject duplicate/missing orders and ambiguous constructors, use invariant round-trip formats, and add dedicated converters for every advertised primitive-like type. Add cross-culture and cross-contract tests.

### P1-9: Incomplete RTP assemblies can grow without a bound

Evidence:

- [H264RtpDepacketizer.cs](src/TripleG3.P2P/Video/Rtp/H264RtpDepacketizer.cs#L9-L25) creates frame assemblies by timestamp.
- The `_maxFrames` check runs only after a marker packet in [H264RtpDepacketizer.cs](src/TripleG3.P2P/Video/Rtp/H264RtpDepacketizer.cs#L43-L54), so packets with unique timestamps and no marker grow `_frames` indefinitely.
- A single FU assembly grows by doubling without a maximum in [H264RtpDepacketizer.cs](src/TripleG3.P2P/Video/Rtp/H264RtpDepacketizer.cs#L142-L159).
- Invalid and trimmed assemblies are removed without calling `ReleaseAll`, so rented buffers are not returned to the pool.

Impact:

Packet loss or untrusted RTP traffic can produce unbounded memory growth or sustained pool depletion. A single stream can also construct an arbitrarily large access unit.

Recommendation:

Enforce maximum frame bytes, maximum NAL bytes, maximum in-flight frames, and an age deadline on every packet insertion. Release all owned buffers on every drop/removal path. Reject packets after a frame is invalidated and expose drop counters for diagnostics.

### P1-10: The release workflow can publish without tests

Evidence:

- [TripleG3-P2P-release.yml](.github/workflows/TripleG3-P2P-release.yml#L42-L55) restores and builds only the package project.
- It then pushes packages in [TripleG3-P2P-release.yml](.github/workflows/TripleG3-P2P-release.yml#L56-L75) without restoring or running either test project.

Impact:

A push to `main` can publish a package while integration or video tests fail to compile or execute. The current local suite would not catch several findings above, but it should still be a minimum release gate.

Recommendation:

Add a pull-request CI workflow and make release depend on a Release build plus both test projects. Test on the exact target runtime, upload test results, and consider a separate manually approved publish job. Add package validation that inspects metadata and the `.nupkg` contents.

### P2-1: Receiver configuration fields are accepted but ignored

Evidence:

- [RtpVideoReceiverConfig.cs](src/TripleG3.P2P/Video/Primitives/RtpVideoReceiverConfig.cs#L3-L10) exposes `PayloadType`, `ExpectedSsrc`, and `JitterBufferMax`.
- [RtpVideoReceiver.cs](src/TripleG3.P2P/Video/RtpVideoReceiver.cs#L12-L33) uses only `LocalPort`; packet processing does not consult the other values.
- The legacy receiver also accepts any SSRC and payload type after parse in [RtpVideoReceiver.cs](src/TripleG3.P2P/Video/Rtp/RtpVideoReceiver.cs#L30-L45).
- [RtpPacket.cs](src/TripleG3.P2P/Video/Rtp/RtpPacket.cs#L20-L35) accounts for CSRC entries but not RTP header extensions or padding.

Impact:

Streams can contaminate each other's state, configured jitter bounds provide no protection, and valid RTP packets with extensions/padding are interpreted as H.264 payload bytes.

Recommendation:

Validate payload type and expected SSRC before updating statistics or reorder state. Enforce the configured jitter deadline, parse or explicitly reject extension/padding packets, and document the supported RFC 3550/RFC 6184 subset.

### P2-2: Subscription storage is not thread-safe and cannot be released

Evidence:

- UDP and TCP use `ConcurrentDictionary<string, List<SubscriptionEntry>>` in [UdpSerialBus.cs](src/TripleG3.P2P/Udp/UdpSerialBus.cs#L20-L23) and [TcpSerialBus.cs](src/TripleG3.P2P/Tcp/TcpSerialBus.cs#L24-L27).
- `SubscribeTo` mutates each `List<T>` without synchronization in [UdpSerialBus.cs](src/TripleG3.P2P/Udp/UdpSerialBus.cs#L130-L135) and [TcpSerialBus.cs](src/TripleG3.P2P/Tcp/TcpSerialBus.cs#L182-L187).
- Receive loops enumerate those lists concurrently, and the API offers no unsubscribe/disposable registration.

Impact:

Subscribing while messages arrive can race list enumeration. In TCP, an escaping enumeration error can terminate the receive loop. Long-lived buses also retain handlers and their captured objects until the bus itself is collected.

Recommendation:

Use immutable snapshots or a locked per-message-type collection. Return an `IDisposable` subscription token, define duplicate-handler behavior, validate null handlers, and report handler failures through logging or an error callback.

### P2-3: The public video surface contains parallel implementations and conflicting abstractions

Evidence:

- There are public sender/receiver implementations in both `TripleG3.P2P.Video` and `TripleG3.P2P.Video.Rtp`.
- There are separate public encoder and decoder interfaces in [IVideoEncoder.cs](src/TripleG3.P2P/Video/IVideoEncoder.cs), [Abstractions/IVideoEncoder.cs](src/TripleG3.P2P/Video/Abstractions/IVideoEncoder.cs), [IVideoDecoder.cs](src/TripleG3.P2P/Video/IVideoDecoder.cs), and [Abstractions/IVideoDecoder.cs](src/TripleG3.P2P/Video/Abstractions/IVideoDecoder.cs).
- [Video/README.md](src/TripleG3.P2P/Video/README.md#L48-L58) says legacy public types may change without normal versioning, but NuGet consumers can still compile directly against them.

Impact:

Fixes do not automatically apply to both paths, consumers can select incompatible similarly named APIs, and public legacy types still create binary-compatibility expectations. The current DI, cipher, lifecycle, and depacketizer defects are partly consequences of this split.

Recommendation:

Choose one implementation and one set of abstractions. Mark compatibility types obsolete and make them delegate to the canonical implementation. For the next major version, internalize them or move experimental RTP types to a separate package with an explicit compatibility policy.

### P2-4: Wire input validation and protocol handling should fail closed

Evidence:

- UDP trusts the header length when slicing in [UdpSerialBus.cs](src/TripleG3.P2P/Udp/UdpSerialBus.cs#L65-L72) without checking for negative, truncated, or trailing lengths.
- Unsupported serializer IDs fall back to `SerializationProtocol.None` in both transports rather than being rejected.
- TCP parses but does not use `MessageType`, applies a hard-coded 10 MB allocation ceiling, and uses host-endian `BitConverter` while UDP explicitly defines little-endian encoding.

Impact:

Malformed traffic is handled through exceptions and silent drops rather than validation and diagnostics. Unknown protocols can be interpreted as another format, and the TCP wire contract is not explicit on big-endian platforms.

Recommendation:

Validate exact frame length before slicing/allocation, reject unknown enum values, make payload limits configurable, and use `BinaryPrimitives` with a documented byte order for both transports. Add malformed/truncated/oversized frame tests.

## Test Gaps to Add First

The existing tests are useful happy-path checks, but the next test tranche should target these regressions:

1. Resolve `IRtpVideoSender` and `IRtpVideoReceiver` from DI and perform an actual transfer.
2. Assert exact bytes from the internal/configuration depacketizer for single NAL and FU-A frames.
3. Exercise a non-no-op cipher for small and fragmented frames, nonzero overhead, and tampering.
4. Drop one RTP packet, then send a later complete frame and verify recovery; repeat across sequence wrap.
5. Assert exact-once TCP delivery when both peers send, then test disconnect/reconnect and concurrent sends.
6. Round-trip `None` values containing `@-@`, empty/null strings, multiple nested records, reordered `[Udp]` attributes, `DateTimeOffset`, decimals, and differing cultures.
7. Verify cancellation and all-destination failure are observable to `SendAsync` callers.
8. Fuzz UDP/TCP headers and RTP payloads with truncation, invalid lengths, unsupported protocol values, and oversized frames.
9. Test subscribe-during-delivery, handler exceptions, unsubscribe, and bus restart/disposal.

Two current assertions deserve replacement:

- [VideoIntegrationTests.cs](tests/TripleG3.P2P.IntegrationTests/VideoIntegrationTests.cs#L96-L100) checks whether an unsigned loss count is greater than or equal to zero, which is always true.
- [H264RtpDepacketizerTests.cs](tests/TripleG3.P2P.VideoTests/H264RtpDepacketizerTests.cs#L20-L27) discards results from the first packet pass and then replays mutated state, so it can miss an erroneous first delivery.

## Recommended Remediation Order

1. Resolve the license and add package license metadata before publishing again.
2. Decide whether video remains experimental. If shipped, fix DI, buffer ownership, cipher semantics, and receiver lifecycle as one coherent API pass.
3. Replace RTP reorder/loss tracking and add bounded assembly limits before testing lossy networks.
4. Redesign TCP peer identity, destination authorization, reconnect cleanup, and single-writer framing.
5. Introduce a new versioned serializer protocol; do not silently alter the existing `None` wire format.
6. Add negative-path tests and require both test projects in PR and release CI.
7. Consolidate or deprecate duplicate video APIs in the next compatible release window.

## P3 Improvements

### Package and build reproducibility

- No repository-owned `NuGet.Config` is tracked, so restore sources and package source mapping come from user/machine configuration. Add a repository configuration with `<clear />` and explicit source mappings for the package families used here so restore behavior is portable to CI and other machines.
- Consider `Directory.Packages.props`; package versions are repeated across the library and test projects.
- .NET 9 is in maintenance support while .NET 10 is the active-support line. Plan an upgrade or a deliberate multi-targeting policy based on the minimum runtime required by consumers.
- Add a solution file or a root build entry point so one command restores, builds, tests, and packs the complete repository.

### Observability and API clarity

- The core package already references logging abstractions, but UDP/TCP do not inject or use `ILogger<T>`. Add structured events for bind/connect/disconnect, malformed frames, deserialization failures, subscriber failures, drops, and reconnect attempts.
- Replace fire-and-forget operations with tracked tasks or explicit ownership. `Dispose` should not start unobserved asynchronous cleanup.
- Validate configuration at startup rather than discovering invalid ports, MTU values, duplicate serializer registrations, or unsupported enum values during traffic.
- Align root and video READMEs with the actual stable API, target framework support policy, limitations, and sample locations.

## What Is Working Well

- The library and both test projects compile cleanly with all warnings treated as errors.
- All current tests pass: 20 integration tests and 16 video tests.
- `ISerialBus`, `IMessageSerializer`, and `ProtocolConfiguration` are small and approachable abstractions.
- UDP fan-out, endpoint de-duplication, mixed message types, and concurrent peer broadcasts have integration coverage.
- UDP and RTP headers use explicit binary primitives, and the RTP parser validates version and CSRC length.
- `EncodedAccessUnit` makes frame ownership visible and provides idempotent pooled-buffer disposal.
- Documentation explains the intended wire format and openly identifies many experimental video limitations.

## Validation Performed

| Check | Result |
| --- | --- |
| `dotnet build src/TripleG3.P2P/TripleG3.P2P.csproj -c Release -warnaserror` | Passed with no warnings or errors. |
| Integration test project, Release | 20 passed, 0 failed. |
| Video test project, Release with warnings as errors | 16 passed, 0 failed. |
| `Convert.ChangeType` probe for `DateTimeOffset` | Failed with invalid cast, confirming the advertised type cannot use the generic conversion path. |
| `TcpClient` endpoint-after-close probe | Endpoint was unavailable after close, confirming the current dictionary cleanup order cannot recover its key. |
| NuGet supply-chain review | NuGet Audit is enabled; source mapping is inherited rather than repository-owned; Central Package Management is not enabled. |

The machine did not have the .NET 9 runtime installed. Tests were built for `net9.0` and executed successfully on the installed .NET 10 runtime with `DOTNET_ROLL_FORWARD=Major`. CI should still run them on the exact supported target runtime.
