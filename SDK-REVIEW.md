# Copilot .NET SDK comparison

**Verified 2026-11-17**, after reviewing [the supplied gist](https://gist.github.com/Arithmomaniac/dbefa44862fdef934558565bbefcc6fb).

## Implemented integration

**The SDK-backed provider is now the default for Copilot**, including CLI and MCP use. It delegates saved-login authentication and both decision protocols to the maintained Copilot runtime. Select `--transport http` explicitly for the direct HTTP implementation. Foundry and OpenAI use their HTTP providers.

The initial research missed this experimental API because it is exposed through generated RPC types rather than the main .NET README's chat examples. `CopilotSdkDecisionProvider` in `Decision.cs` now uses it, while the original proof of concept remains a separate file-based example.

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

The application's normal build acquires and bundles the matching runtime; SDK 1.0.34 pins CLI 1.0.112. `--copilot-cli-path` can select an existing compatible executable. The standalone probe opts out of downloading and takes an explicit executable path, then checks its version/protocol. The gist reports that protocol-4 CLI 1.0.113-1 is incompatible with the protocol-3 SDK.

Run the example with an existing **1.0.112** installation:

```powershell
dotnet run --file examples/CopilotSdkProbe.cs -- (Get-Command copilot).Source
```

This makes two live inference calls, prints the decision catalog and typed results, and exits. Authentication remains owned by the runtime. No machine-wide feature flag is set.

## Tradeoffs that matter for this utility

| Area | SDK 1.0.34 / runtime 1.0.112 | Explicit HTTP transport |
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
| Runtime/service default | **Luna** on the tested account; app explicitly supplies its Microsoft default | App supplies **Microsoft-Decision-1** |

These SDK catalog limits were read from the live runtime, not inferred from the chat-model limits. Microsoft-Decision-1 additionally reported a 32,768-token budget for context plus a question. SDK choice values are restricted to 1-64 characters using letters, digits, and `._:/@-` according to its published type documentation. This SDK version has no image-context variant. Image inference through the existing HTTP adapter has not been live-tested here.

The one probe took about **2.8 seconds** for startup/status/authentication. The subsequent decision calls took approximately **0.72 seconds** and **0.53 seconds** end to end. These are individual observations, not a controlled comparison with HTTP. A persistent SDK client amortizes initialization across MCP calls; spawning a new runtime for each tool invocation would lose that benefit.

### Result semantics

The runtime normalizes probabilities and calculates `Confidence` as `(p_max - 1/n) / (1 - 1/n)`. The backend's own value is separately exposed as **`VendorConfidence`**. For one live Luna score, SDK confidence was about `0.385`, while vendor confidence was `0.39`.

The adapter preserves **`VendorConfidence` in `confidence`**, exposes SDK `Confidence` separately as `normalizedConfidence`, and includes `tied` for choice answers. Missing vendor confidence stays missing. Probabilities are the SDK-normalized values; HTTP retains the original provider values. Refusals remain refusals, and requested versus upstream model names remain distinct.

`--raw` returns the serialized normalized SDK result for SDK calls, or the upstream JSON for HTTP calls. Normal results include `transport` and `rawFormat` to make this distinction explicit. SDK usage is mapped to the common snake-case token fields without inventing unreported totals.

### Lifecycle and configuration

1. `CopilotSdkDecisionProvider : IDecisionProvider` starts its client lazily, serializes initialization, and shares that client across MCP tool calls. `DecisionService` disposes it at shutdown.
2. Models come from `Rpc.Models.ListAsync().DecisionModels`; the returned catalog exposes effective limits and the runtime's default separately from the application's default. Discovery is cached for five minutes.
3. Every `AskAsync` call explicitly receives the application's resolved model. The Microsoft default and per-call overrides therefore take precedence over the runtime's Luna default.
4. The adapter checks question/option/level counts, choice-label syntax, serialized byte size, and JSON nesting before inference. The runtime enforces its token budget and remaining validation rules. Images require HTTP.
5. `--timeout` remains an overall operation budget. Each SDK RPC uses at most `min(timeout, 30)` seconds, including the runtime's own queue/authentication/inference/parsing work.
6. Saved-login and automatic authentication use the runtime directly. Explicit `--auth gh`, `--auth env`, or `--account` uses the existing targeted credential resolver and passes the credential privately to the client.
7. Structured SDK failures become actionable CLI/MCP errors. The utility does not replay failed inference or switch transports automatically.

This sessionless API is Copilot-account based. Foundry's deployment paths and Entra authentication remain served by the Foundry provider.

### Account-list compatibility issue

Live testing of `account.getAllUsers` with SDK 1.0.34/runtime 1.0.112 failed because the generated `AuthInfoGhCli` type requires a `token` property that the runtime redacts. An unrelated GitHub CLI entry can therefore prevent the whole account roster from deserializing. The application avoids this API: ordinary saved-login calls use `UseLoggedInUser`, and explicit `--account` calls reuse the existing login resolver. This preserves named-account selection without creating a chat session or changing the user's active CLI account.

## References

- [Original gist](https://gist.github.com/Arithmomaniac/dbefa44862fdef934558565bbefcc6fb).
- [Published .NET SDK 1.0.34](https://www.nuget.org/packages/GitHub.Copilot.SDK/1.0.34).
- [v1.0.34 generated .NET RPC implementation](https://raw.githubusercontent.com/microsoft/copilot-sdk/v1.0.34/dotnet/src/Generated/Rpc.cs): `ServerDecisionsApi`, `DecisionModelLimits`, `DecisionContext`, and `DecisionAnswer` types.
- [v1.0.34 .NET authentication/runtime options](https://github.com/microsoft/copilot-sdk/blob/v1.0.34/dotnet/README.md).
- [v1.0.34 SDK protocol version](https://github.com/microsoft/copilot-sdk/blob/v1.0.34/dotnet/src/SdkProtocolVersion.cs).
- [Node.js convenience facade](https://github.com/microsoft/copilot-sdk/blob/v1.0.34/nodejs/src/decisions.ts).
