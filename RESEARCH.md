# Decision-model access research

**Checked: 2026-11-17.** Public documentation was reviewed and live HTTP calls were made using the existing authenticated accounts on this Windows machine. This is a point-in-time account-specific verification.

## Conclusions

1. **Both requested decision models are accessible through the Copilot hosting service for the tested login.** Their exact catalog IDs are `microsoft-decision-1` and `gpt-6-luna-decisions`.
2. Both appear in authenticated `GET https://api.githubcopilot.com/models` with `capabilities.type: "decisions"` and `model_picker_enabled: false`. A client that only lists chat/picker models will miss them.
3. They require **different wire protocols**. One provider/authentication implementation can support both by selecting a protocol adapter from `supported_endpoints`.
4. Existing **Copilot CLI and GitHub CLI browser logins both worked**. No PAT was created. HTTP still uses an OAuth bearer token internally; the utility retrieves it from the existing credential store/helper.
5. Microsoft Foundry provides documented endpoints for both models, with Entra ID or key authentication. Foundry billing is separate from the Copilot subscription.

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
- A real MCP client initialized the stdio server, discovered its typed tools, and successfully invoked both models using the same saved Copilot CLI login. The updated verification run passed 13 offline test groups plus the live MCP checks (150 assertions), including an omitted tool-call model inheriting `--default-model` and a per-call model override.
- `microsoft-decision-1` returned underlying model name `microsoft-decision-1.1`; `gpt-6-luna-decisions` returned `gpt-6-luna`. Both requested and returned names are preserved in results.
- The support-ticket choice was `billing` in both cases. This verifies integration, not general model accuracy or calibration.
- Raw response fixtures are in `tests/fixtures/`. They contain sample answers and usage, not credentials.
- The offline suite checks Foundry URLs, deployment-name handling, API-key headers, common result handling, and MCP protocol behavior.

No Azure deployment or direct OpenAI key was provided for live verification. Image input is mapped by the Decisions adapter but was not live-tested. Other accounts, plans, organizational policies, or regions can expose different availability.

## Authentication findings

The official Copilot CLI docs describe OAuth browser/device login, OS credential storage under service `copilot-cli`, and fallback to `gh auth token` [1]. The SDK also documents stored-user authentication [2]. The utility reuses these credentials without starting an agent session.

On this machine, the current CLI configuration was **JSON with comments** and used `lastLoggedInUser` / `loggedInUsers`. Older and newer credential target layouts coexisted. One old stored credential returned 401, while newer layouts worked. The implementation therefore reads only entries for the selected account and prefers the newest Windows credential by its stored timestamp.

Older community integrations exchange a GitHub token at `/copilot_internal/v2/token` before using the Copilot gateway [3]. In this investigation that exchange returned **403 for the GitHub CLI credential**, while sending the same credential directly to the gateway returned 200. The implemented path is the working **direct bearer-token** path; it does not depend on that exchange.

The OS credential-store layout and direct inference interface are implementation details, not a public compatibility guarantee. The portable saved-login alternative is the GitHub CLI helper. Reauthentication uses the official `copilot login` or `gh auth login` flow.

## Copilot SDK, normal Copilot, and MCP

The current Copilot SDK documentation describes agent sessions and message submission, including generated structured outputs [2, 4]. It does not document a public method accepting the `systemone` or `decisions` request contracts reviewed here. Setting a session's `Model` field does not convert an agent/chat request into either contract.

Consequently:

- Use direct decision HTTP requests in this utility.
- Expose the utility as MCP to let a Copilot/other agent invoke it as a tool.
- Continue using the same logged-in Copilot account for the utility's authentication.

This is technically verified access to Copilot's authenticated inference service, **not an assertion that GitHub has published a supported public Decisions API for arbitrary applications**. The official general Copilot model list [5] covers the regular product surfaces; the authenticated catalog is the source used here for decision routes.

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
    +-- CopilotDecisionProvider ---- saved login + account model discovery
    +-- FoundryDecisionProvider ---- Entra / API key + deployment configuration
    +-- OpenAiDecisionProvider ----- API key
    |
IDecisionProtocol
    +-- SystemOneProtocol ---------- state, named object, noul, criteria
    +-- DecisionsProtocol ---------- input, named array, predicate, choices/levels
```

**One Copilot provider is sufficient for both models; two protocol adapters are necessary.** Foundry reuses the same adapters while supplying its own paths, credentials, and deployment names. Arbitrary Foundry deployment names make an explicit protocol setting preferable to guessing from the name.

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
