# Copilot .NET SDK comparison

**Verified 2026-11-17**, after reviewing [the supplied gist](https://gist.github.com/Arithmomaniac/dbefa44862fdef934558565bbefcc6fb).

## Recommendation

**Prefer an SDK-backed Copilot provider for the long-running MCP server**, provided the SDK's request limits fit the workload. It delegates authentication and the two decision protocols to the maintained Copilot runtime. Keep direct HTTP available for workloads needing capabilities outside that SDK contract, and for the existing Foundry/OpenAI providers.

The initial research missed this experimental API because it is exposed through generated RPC types rather than the main .NET README's chat examples. Both models really can be called with the published .NET SDK. The application in `Decision.cs` currently uses HTTP; the SDK proof of concept is a separate file-based example.

## What was tested

- **Package:** `GitHub.Copilot.SDK` **1.0.34**.
- **Runtime:** Copilot CLI **1.0.112**, reporting **protocol 3**.
- **Application:** .NET 11 / C# 15 file-based app, [examples/CopilotSdkProbe.cs](examples/CopilotSdkProbe.cs).
- **Authentication:** `UseLoggedInUser = true`. The probe cleared token variables from the child process's environment, supplied no `GitHubToken`, and did not use `gh auth token` or read the OS credential store. The runtime reported an authenticated user and used the saved login.
- **Inference:** successful boolean, choice, and score answers from **both** `microsoft-decision-1` and `gpt-6-luna-decisions`.
- **Returned backend names:** `microsoft-decision-1.1` and `gpt-6-luna`.
- **No chat session:** no `CreateSessionAsync` or `SendAsync` calls.

The Node.js gist's `client.decisions.yesNo()` / `ask()` / `listModels()` helpers have these .NET counterparts:

| Node.js facade | Published .NET API |
| --- | --- |
| `client.decisions.listModels()` | `(await client.Rpc.Models.ListAsync()).DecisionModels` |
| `client.decisions.ask(...)` | `await client.Rpc.Decisions.AskAsync(...)` |
| `client.decisions.yesNo(...)` | Ask one `DecisionQuestionBoolean`; inspect `DecisionAnswerBoolean.Probability` or `DecisionAnswerRefused` |
| `result.byName` | Build a lookup from the typed `Answers` collection |

The .NET method accepts `DecisionContextText` or `DecisionContextJson`, and a list of `DecisionQuestionBoolean`, `DecisionQuestionChoice`, and/or `DecisionQuestionScore`. Changing its `model` argument is sufficient to choose either model family.

## Runtime configuration

Enable this **in the runtime child's environment**:

```text
COPILOT_CLI_ENABLED_FEATURE_FLAGS=DECISIONS_API
```

`CopilotClientOptions.Environment` replaces the inherited environment, so clone it before adding the flag. The example does this and scopes the change to the child process. The experimental .NET types require:

```csharp
#pragma warning disable GHCP001
```

The NuGet package normally acquires a matching runtime; 1.0.34 pins CLI 1.0.112. The example opts out of that download and takes an explicit executable path, then checks the reported version/protocol. This reproduced the gist's compatible pair using the CLI already installed here. The gist reports that protocol-4 CLI 1.0.113-1 is incompatible with its protocol-3 SDK. An arbitrarily newer global CLI is therefore not a substitute for the package's matching runtime.

Run the example with an existing **1.0.112** installation:

```powershell
dotnet run --file examples/CopilotSdkProbe.cs -- (Get-Command copilot).Source
```

This makes two live inference calls, prints the decision catalog and typed results, and exits. Authentication remains owned by the runtime. No machine-wide feature flag is set.

## Tradeoffs that matter for this utility

| Area | SDK 1.0.34 / runtime 1.0.112 | Current HTTP utility |
| --- | --- | --- |
| Saved-login reuse | Runtime handles it | Custom OS-store readers and `gh` helper |
| Protocol translation | One typed API for both model families | Two HTTP protocol adapters |
| Deployment | Needs compatible Copilot runtime assets | Ordinary .NET app dependencies |
| Startup | Runtime startup/authentication before inference | Direct HTTP setup |
| Max questions | **32**, reported by the runtime catalog | Client accepts up to 400; upstream limits also apply |
| Max choices | **10**, reported by the runtime catalog | Client accepts up to 255; upstream limits also apply |
| Score levels | **2-10** | 2-10 |
| Request size | **262,144 bytes (256 KiB)** | Upstream/request context limits |
| JSON depth | **32** | JSON/parser and upstream limits |
| Deadline | **250-30,000 ms**, default 5,000 ms | Configurable end-to-end timeout, default 60 seconds |
| Context types | Text; JSON object or array of objects | Text/JSON; Decisions image mapping is also implemented |
| Raw response | Normalized SDK result | Original provider JSON with `--raw` |
| Default model | **Luna** on the tested account | **Microsoft-Decision-1** |

These SDK catalog limits were read from the live runtime, not inferred from the chat-model limits. Microsoft-Decision-1 additionally reported a 32,768-token budget for context plus a question. SDK choice values are restricted to 1-64 characters using letters, digits, and `._:/@-` according to its published type documentation. This SDK version has no image-context variant. Image inference through the existing HTTP adapter has not been live-tested here.

The one probe took about **2.8 seconds** for startup/status/authentication. The subsequent decision calls took approximately **0.72 seconds** and **0.53 seconds** end to end. These are individual observations, not a controlled comparison with HTTP. A persistent SDK client amortizes initialization across MCP calls; spawning a new runtime for each tool invocation would lose that benefit.

### Result semantics

The runtime normalizes probabilities and calculates `Confidence` as `(p_max - 1/n) / (1 - 1/n)`. The backend's own value is separately exposed as **`VendorConfidence`**. For one live Luna score, SDK confidence was about `0.385`, while vendor confidence was `0.39`.

The current utility preserves the provider's confidence and probabilities. An SDK adapter must deliberately map these fields rather than silently changing the meaning of its existing `confidence` property. It should also preserve refusal results and distinguish `Model` from `UpstreamModel`. The original HTTP JSON is not exposed by this typed result, so `--raw` would need an explicitly documented SDK representation or the HTTP transport.

### Suggested integration

1. Implement `CopilotSdkDecisionProvider : IDecisionProvider`, sharing one long-lived `CopilotClient` within the MCP server.
2. Discover models through `Rpc.Models.ListAsync().DecisionModels`; use the effective runtime limits.
3. Pass the utility's resolved model explicitly to `AskAsync`. Otherwise the runtime currently chooses Luna, conflicting with the requested Microsoft default.
4. Map the existing predicate/choice/score request types and typed SDK answers; validate SDK-specific limits before calling.
5. Keep the existing provider interface and CLI/MCP result shape, with explicit handling of the SDK deadline, confidence, and raw-response differences.

This sessionless API is Copilot-account based. Foundry's deployment paths and Entra authentication remain served by the Foundry provider.

## References

- [Original gist](https://gist.github.com/Arithmomaniac/dbefa44862fdef934558565bbefcc6fb).
- [Published .NET SDK 1.0.34](https://www.nuget.org/packages/GitHub.Copilot.SDK/1.0.34).
- [v1.0.34 generated .NET RPC implementation](https://raw.githubusercontent.com/microsoft/copilot-sdk/v1.0.34/dotnet/src/Generated/Rpc.cs): `ServerDecisionsApi`, `DecisionModelLimits`, `DecisionContext`, and `DecisionAnswer` types.
- [v1.0.34 .NET authentication/runtime options](https://github.com/microsoft/copilot-sdk/blob/v1.0.34/dotnet/README.md).
- [v1.0.34 SDK protocol version](https://github.com/microsoft/copilot-sdk/blob/v1.0.34/dotnet/src/SdkProtocolVersion.cs).
- [Node.js convenience facade](https://github.com/microsoft/copilot-sdk/blob/v1.0.34/nodejs/src/decisions.ts).
