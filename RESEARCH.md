# Decision-model access research

**Checked: 2026-11-17.** Public documentation was reviewed and live HTTP and .NET SDK calls were made using the existing authenticated accounts on this Windows machine. This is a point-in-time account-specific verification. The SDK findings below correct the initial review, following the supplied gist.

## Conclusions

1. **Both requested decision models are accessible through the Copilot hosting service for the tested login.** Their exact catalog IDs are `microsoft-decision-1` and `gpt-6-luna-decisions`.
2. Both appear in authenticated `GET https://api.githubcopilot.com/models` with `capabilities.type: "decisions"` and `model_picker_enabled: false`. A client that only lists chat/picker models will miss them.
3. They require **different wire protocols**. One provider/authentication implementation can support both by selecting a protocol adapter from `supported_endpoints`.
4. Existing **Copilot CLI and GitHub CLI browser logins both worked**. No PAT was created. The default SDK transport delegates saved-login authentication to the runtime; the explicit HTTP transport uses the existing credential store/helper.
5. Microsoft Foundry provides documented endpoints for both models, with Entra ID or key authentication. Foundry billing is separate from the Copilot subscription.
6. **The published .NET Copilot SDK can call both models directly through its sessionless RPC API and is now the application's default Copilot transport.** `GitHub.Copilot.SDK` 1.0.34 and runtime 1.0.112 (protocol 3) were live-tested with `UseLoggedInUser = true` and the `DECISIONS_API` feature flag. `--transport http` selects the HTTP implementation. See [SDK-REVIEW.md](SDK-REVIEW.md) for effective limits and result semantics.

## Verified Copilot routes

| Catalog model ID | Advertised endpoint | Request contract | Answer contract |
| --- | --- | --- | --- |
| `microsoft-decision-1` | `POST /v1/systemone` | `state`; `questions` object keyed by name; `noul`, `choice`, `score`; `criteria` | `answers` object; `noul` numeric probability; probability maps |
| `gpt-6-luna-decisions` | `POST /v1/decisions` | `input`; named `questions` array; `predicate`, `choice`, `score`; `choices` / `levels` | `answers` array; `probability`; probability arrays |

The ordinary `gpt-6-luna` Copilot entry advertises `/responses` and `ws:/responses` and has type `chat`. It is distinct from the decision entry. The utility does not silently turn a chat-model request into a decision-model request.

Successful probes used:

```http
Authorization: Bearer <existing GitHub OAuth credential>
Accept: application/json
Content-Type: application/json
User-Agent: ghcp-decision/0.1.0
Editor-Version: ghcp-decision/0.1.0
Copilot-Integration-Id: copilot-developer-cli
X-Initiator: user
```

The utility adds a fresh `X-Request-Id`. This header set was tested; the experiment did not establish that every header is mandatory.

### Example Microsoft request

```json
{
  "model": "microsoft-decision-1",
  "state": "I was charged twice. Please refund the duplicate charge.",
  "questions": {
    "refund": {
      "type": "noul",
      "instructions": "Does the customer request a refund?"
    },
    "team": {
      "type": "choice",
      "instructions": "Which team should handle this?",
      "criteria": { "billing": "Payments and refunds", "support": "Other requests" }
    }
  }
}
```

### Equivalent Luna request

```json
{
  "model": "gpt-6-luna-decisions",
  "input": "I was charged twice. Please refund the duplicate charge.",
  "questions": [
    {
      "name": "refund",
      "type": "predicate",
      "instructions": "Does the customer request a refund?"
    },
    {
      "name": "team",
      "type": "choice",
      "instructions": "Which team should handle this?",
      "choices": [
        { "value": "billing", "description": "Payments and refunds" },
        { "value": "support", "description": "Other requests" }
      ]
    }
  ]
}
```

## What was actually verified

- Authenticated discovery through `gh` returned both decision models, with the paths above.
- Direct requests through the GitHub CLI credential returned **HTTP 200** for both models, with predicate, choice, and score questions in each request.
- The built .NET utility reused the selected Copilot CLI account from Windows Credential Manager and successfully discovered and called both models.
- A real MCP client initialized the stdio server, discovered its typed tools, and successfully invoked both models using the same saved Copilot CLI login, including an omitted tool-call model inheriting `--default-model` and a per-call model override. The test suite also covers the SDK provider's lifetime, effective limits, account selection, and confidence mapping.
- `microsoft-decision-1` returned underlying model name `microsoft-decision-1.1`; `gpt-6-luna-decisions` returned `gpt-6-luna`. Both requested and returned names are preserved in results.
- The support-ticket choice was `billing` in both cases. This verifies integration, not general model accuracy or calibration.
- Raw response fixtures are in `tests/fixtures/`. They contain sample answers and usage, not credentials.
- The offline suite checks Foundry URLs, deployment-name handling, API-key headers, common result handling, and MCP protocol behavior.
- A separate .NET SDK probe called `client.Rpc.Decisions.AskAsync(...)` with boolean, choice, and score questions for each model. The runtime reported authenticated `user` credentials; the probe supplied no token and did not read the credential store itself. Both calls succeeded, with the expected upstream model names and `billing` selection.

No Azure deployment or direct OpenAI key was provided for live verification. Image input is mapped by the Decisions adapter but was not live-tested. Other accounts, plans, organizational policies, or regions can expose different availability.

## Authentication findings

The official Copilot CLI docs describe OAuth browser/device login, OS credential storage under service `copilot-cli`, and fallback to `gh auth token` [1]. The SDK also documents stored-user authentication [2]. The utility uses the runtime's authentication for its default SDK transport, and its own credential readers for HTTP, without starting an agent session.

On this machine, the current CLI configuration was **JSON with comments** and used `lastLoggedInUser` / `loggedInUsers`. Older and newer credential target layouts coexisted. One old stored credential returned 401, while newer layouts worked. The HTTP implementation therefore reads only entries for the selected account and prefers the newest Windows credential by its stored timestamp. SDK saved-login mode delegates this storage behavior to the runtime.

Older community integrations exchange a GitHub token at `/copilot_internal/v2/token` before using the Copilot gateway [3]. In this investigation that exchange returned **403 for the GitHub CLI credential**, while sending the same credential directly to the gateway returned 200. The HTTP transport uses the working **direct bearer-token** path. SDK authentication is managed by the runtime.

The OS credential-store layout and direct inference interface are implementation details, not a public compatibility guarantee. The portable saved-login alternative is the GitHub CLI helper. Reauthentication uses the official `copilot login` or `gh auth login` flow.

## Copilot SDK, normal Copilot, and MCP

**Correction:** the initial review focused on the SDK README and missed the public experimental APIs in its generated RPC surface. The supplied gist [12] demonstrates a sessionless Node.js facade; the published .NET SDK exposes the same runtime operation through `client.Rpc.Decisions.AskAsync(...)` [13].

The .NET equivalents verified in package 1.0.34 are:

- `client.Rpc.Models.ListAsync()` and its `DecisionModels` property for discovery.
- `client.Rpc.Decisions.AskAsync(...)` for both model families.
- `DecisionContextText` / `DecisionContextJson` and typed boolean, choice, score, and refusal results.
- `UseLoggedInUser = true` for runtime-managed authentication.

These calls need no chat session. The runtime must have `COPILOT_CLI_ENABLED_FEATURE_FLAGS=DECISIONS_API`, and the experimental C# API requires an explicit `GHCP001` diagnostic opt-in. The verified SDK/runtime pair is 1.0.34 / 1.0.112, using protocol 3; the NuGet package pins that runtime version in its build props [14]. The gist reports a protocol mismatch with a newer protocol-4 CLI, so compatible version selection matters.

The implemented SDK-backed provider shares one persistent runtime client for maintained authentication, discovery, and protocol handling. Its effective limits and normalization differ from HTTP; [SDK-REVIEW.md](SDK-REVIEW.md) records the verified differences and mapping. The SDK's default on this account was Luna, so the utility always passes its resolved Microsoft default or explicit model override.

Copilot's raw HTTP endpoints remain internal, and the typed SDK Decisions surface is explicitly experimental. The official general Copilot model list [5] covers the regular product surfaces; use decision-specific discovery for this workload.

A public community implementation independently reported successful Microsoft-Decision-1 requests through Copilot Enterprise on November 2, using `/v1/systemone`; its merged implementation specifically retains hidden classifier models [3]. The Luna Copilot route in this utility is supported by this session's live discovery and inference, rather than inferred from the ordinary chat-model listing.

## Foundry and direct OpenAI

Microsoft documents both Foundry endpoints:

| Deployment | Resource-relative path | Key details |
| --- | --- | --- |
| Microsoft-Decision-1 | `/providers/microsoft/v1/systemone` | Text/JSON, `noul`/`choice`/`score`; `model` is deployment name [6] |
| GPT-6 Luna | `/openai/v1/decisions` | `predicate`/`choice`/`score`, text and images; `model` is deployment name [7] |

Both support Entra ID (`https://cognitiveservices.azure.com/.default`) and API keys. The documented routes do not need an added `api-version` query. The current Azure Decisions documentation describes regional rollout and excludes East US 2; deployment availability alone should not be treated as proof that the Decisions route works in every region [7].

Direct OpenAI uses `POST https://api.openai.com/v1/decisions` with **`model: "gpt-6-luna"`**, not Copilot's `gpt-6-luna-decisions` ID [8]. It uses a separate OpenAI API credential and billing account.

OpenRouter and other gateways also publish decision-model access, but those services use their own accounts and billing. They do not satisfy the goal of consuming the existing Copilot hosting account, so the implemented providers concentrate on Copilot, Foundry, and direct OpenAI.

The old **GitHub Models** inference product is not an alternative: GitHub's current documentation says it was fully retired on **2026-07-30**, and explicitly distinguishes it from Copilot [9]. Older tutorials using its playground/catalog/inference endpoints are therefore not applicable.

## Billing and architecture decision

The returned Copilot catalog did not supply a decision-model price. It included a usage-based-billing message saying model multipliers no longer apply. Foundry/OpenAI prices must not be presented as Copilot prices. The utility returns provider usage as supplied and does not invent a dollar estimate or claim these requests are unlimited/free.

Implementation split:

```text
CLI / MCP
    |
DecisionService + common typed request/result
    |
IDecisionProvider
    +-- CopilotSdkDecisionProvider - default: sessionless SDK + runtime login
    +-- CopilotDecisionProvider ---- explicit HTTP + account model discovery
    +-- FoundryDecisionProvider ---- Entra / API key + deployment configuration
    +-- OpenAiDecisionProvider ----- API key
    |
IDecisionProtocol
    +-- SystemOneProtocol ---------- state, named object, noul, criteria
    +-- DecisionsProtocol ---------- input, named array, predicate, choices/levels
```

**Each Copilot transport supports both models.** The SDK implementation delegates protocol translation to the runtime. HTTP needs the two protocol adapters, which Foundry also reuses with its own paths, credentials, and deployment names. Arbitrary Foundry deployment names make an explicit protocol setting preferable to guessing from the name.

## Sources

1. [GitHub: Authenticating Copilot CLI](https://docs.github.com/en/copilot/how-tos/copilot-cli/set-up-copilot-cli/authenticate-copilot-cli) — login flows, supported token types, credential stores, and `gh` fallback.
2. [Copilot SDK: Authentication](https://github.com/github/copilot-sdk/blob/main/docs/auth/authenticate.md) — signed-in user and OAuth authentication; the SDK is currently transitioning to Microsoft Copilot SDK naming.
3. [Pi issue #11678](https://github.com/earendil-works/pi/issues/11678) and [merged PR #11679](https://github.com/earendil-works/pi/pull/11679) — independent working Microsoft-Decision-1 Copilot implementation. [OAuth implementation](https://github.com/earendil-works/pi/blob/main/packages/ai/src/auth/oauth/github-copilot.ts) documents its exchange-based approach.
4. [Copilot SDK .NET documentation](https://github.com/github/copilot-sdk/blob/main/dotnet/README.md) — session/message APIs and structured-output API surface reviewed.
5. [GitHub: Supported AI models in Copilot](https://docs.github.com/en/copilot/reference/ai-models/supported-models).
6. [Microsoft Learn: Deploy and use Microsoft-Decision-1 in Foundry](https://learn.microsoft.com/en-us/azure/foundry/foundry-models/how-to/use-foundry-models-microsoft-decision).
7. [Microsoft Learn: Azure OpenAI Decisions API](https://learn.microsoft.com/en-us/azure/foundry/openai/how-to/decisions).
8. [OpenAI: Decisions API guide](https://developers.openai.com/api/docs/guides/decisions) — current GA contract, question types, refusals, JSON/image inputs, and pricing.
9. [GitHub: GitHub Models retirement](https://docs.github.com/en/billing/concepts/product-billing/github-models).
10. [Microsoft Learn: File-based apps](https://learn.microsoft.com/en-us/dotnet/core/sdk/file-based-apps) and [C# 15](https://learn.microsoft.com/en-us/dotnet/csharp/whats-new/csharp-15) — .NET 11 file-based apps, `#:include`, closed hierarchies, and collection-expression arguments.
11. [Official MCP C# SDK](https://github.com/modelcontextprotocol/csharp-sdk) — stdio hosting and structured tools.
12. [Supplied gist: Copilot decision models through HTTP or SDK](https://gist.github.com/Arithmomaniac/dbefa44862fdef934558565bbefcc6fb) — Node.js sessionless helpers, feature flag, and verified package/runtime pairing.
13. [Published v1.0.34 .NET generated RPC source](https://raw.githubusercontent.com/microsoft/copilot-sdk/v1.0.34/dotnet/src/Generated/Rpc.cs) — `ServerDecisionsApi.AskAsync`, decision request/result types, limits, and experimental annotations. Also verified against the installed NuGet assembly and XML documentation.
14. [GitHub.Copilot.SDK 1.0.34](https://www.nuget.org/packages/GitHub.Copilot.SDK/1.0.34) — published package. Its installed `build/GitHub.Copilot.SDK.props` pins `CopilotCliVersion` to `1.0.112`.
