# GHCP Decision

A **single-file .NET 11 / C# 15 app** for decision models, usable as a CLI or an MCP stdio server. No application `.csproj` is needed; dependencies are declared in `Decision.cs`.

**Both Copilot models were called successfully on this machine using existing browser-login credentials.** No PAT was created or supplied. See [RESEARCH.md](RESEARCH.md) for the dated findings and sources.

| Provider | Model / deployment | Default API | Authentication |
| --- | --- | --- | --- |
| `copilot` | `microsoft-decision-1` | SDK `decisions.ask` | Runtime-managed saved login |
| `copilot` | `gpt-6-luna-decisions` | SDK `decisions.ask` | Same implementation and login |
| `foundry` | Your Microsoft-Decision-1 deployment name | `/providers/microsoft/v1/systemone` | Entra ID / `az login`, or API key |
| `foundry` | Your GPT-6 Luna deployment name | `/openai/v1/decisions` | Entra ID / `az login`, or API key |
| `openai` | `gpt-6-luna` | `/v1/decisions` | OpenAI API key |

Copilot uses the published .NET SDK's **experimental sessionless Decisions API** by default. A single persistent SDK client handles both models and reuses the saved login. `--transport http` selects the direct HTTP implementation explicitly. Foundry and OpenAI use their documented HTTP APIs. See [SDK-REVIEW.md](SDK-REVIEW.md) for the comparison and verified runtime behavior.

## Quick start

Requires the **.NET 11 SDK**. For Copilot, sign in with either `copilot login` or `gh auth login --web --hostname github.com` if you have not already done so. The first build acquires the runtime matched to `GitHub.Copilot.SDK` 1.0.34 (CLI 1.0.112, protocol 3). The app enables `DECISIONS_API` in that child process automatically.

```powershell
# Discover the decision models available to your login.
dotnet run --file Decision.cs -- models

# Microsoft-Decision-1 is the default.
dotnet run --file Decision.cs -- decide --input examples/ticket.json

# Same input, same hosting subscription, different decision model.
dotnet run --file Decision.cs -- decide --model gpt-6-luna-decisions --input examples/ticket.json

# Force reuse of the Copilot CLI login, ignoring token environment variables.
dotnet run --file Decision.cs -- decide --auth copilot-cli --input examples/ticket.json

# Alternatively, reuse the GitHub CLI's saved login.
dotnet run --file Decision.cs -- models --auth gh

# Explicit direct HTTP, without starting an SDK runtime.
dotnet run --file Decision.cs -- decide --transport http --input examples/ticket.json
```

The exact Copilot Luna ID is **`gpt-6-luna-decisions`**, plural. The singular `gpt-6-luna-decision` is accepted as a convenience alias. The ordinary Copilot chat model `gpt-6-luna` is a different catalog entry and is not substituted automatically.

### Quick questions

```powershell
# Predicate: returns the probability that the answer is yes.
dotnet run --file Decision.cs -- decide --text "Please refund my payment." --question "Does the customer request a refund?"

# Choice: repeat --choice with label=description.
dotnet run --file Decision.cs -- decide --text "I was charged twice." --question "Which team should handle this?" --choice "billing=Payments and refunds" --choice "support=Other requests"

# Score: repeat --level, from lowest to highest.
dotnet run --file Decision.cs -- decide --text "Checkout is down for everyone." --question "How severe is this incident?" --level Cosmetic --level Minor --level Major --level Critical

# Read request JSON from stdin.
Get-Content -Raw examples/ticket.json | dotnet run --file Decision.cs -- decide --input -
```

Use `--name` to name a shorthand question. `--raw` returns the **normalized SDK RPC JSON** with the SDK transport, or the original upstream JSON with HTTP. `--timeout` sets the whole operation's budget (default 60 seconds), including SDK startup and discovery. Each SDK inference RPC has a separate maximum of **30 seconds**; setting a larger overall timeout does not extend that RPC limit. CLI results go to stdout; errors go to stderr. Exit codes: `0` success, `1` error, `130` cancelled. A typed model refusal is a successful response with `type: "refusal"`.

## Common request and result

The same request works with either protocol:

```json
{
  "state": "I was charged twice. Please refund the duplicate charge.",
  "questions": {
    "refund": {
      "type": "predicate",
      "instructions": "Does the customer request a refund?"
    },
    "team": {
      "type": "choice",
      "instructions": "Which team should handle this?",
      "choices": {
        "billing": "Payments and refunds",
        "support": "Other requests"
      }
    },
    "severity": {
      "type": "score",
      "instructions": "How severe is the issue?",
      "levels": ["Low", "Medium", "High"]
    }
  }
}
```

- `state` accepts text or JSON evidence. The SDK receives objects/arrays of objects as JSON and other values as text. HTTP System One receives JSON directly; HTTP Decisions text-encodes JSON evidence for gateway compatibility. JSON keys are treated as data.
- The common contract allows 1-400 named questions, 2-255 choices, and 2-10 distinct score levels. **SDK limits are stricter** and validated against its model catalog: currently 32 questions, 2-10 choices, 2-10 levels, 256 KiB serialized request size, and JSON depth 32. SDK choice labels must be 1-64 ASCII letters/digits or `._:/@-`. The runtime also enforces token limits. Run `models` for effective limits.
- An optional `images` array accepts HTTPS image URLs or base64 image data URLs with **HTTP Decisions**. The SDK transport rejects images before starting the runtime. Microsoft-Decision-1 is text-only. Image inference was not live-tested here.
- Results have `provider`, `transport`, `requestedModel`, the actual returned `model`, `protocol`, `credentialSource`, `elapsedMilliseconds`, `answers`, `usage`, and `rawFormat`. SDK `protocol` is `decisions.ask`; HTTP reports `systemone` or `decisions`. `rawFormat` distinguishes `copilot-sdk-json` from `provider-json`.
- HTTP `usage` is unchanged. SDK usage is mapped to the same snake-case token field names; unreported fields such as totals are not invented. The SDK-native representation is available with `--raw`.
- `answers` is always keyed by question name. Predicates contain `probability`; choices contain `choice`, a label-to-probability map, and optional `confidence`; scores contain `score`, ordered `probabilities`, `levels`, and optional `confidence`.
- Scores are probability-weighted **zero-based** level indexes, not percentages. `confidence` always means the backend's reported confidence, including when transported through the SDK. SDK results also expose `normalizedConfidence` (distribution concentration), and choice results expose `tied`. The SDK normalizes probabilities before returning them; this adapter preserves those values and never substitutes normalized confidence for missing vendor confidence.
- A refusal remains a `refusal`. Missing/mismatched answers and invalid probabilities produce an error.

The utility does not request generated explanations. These APIs return bounded judgments rather than prose.

## Authentication

### Copilot: reuse an existing login

With the default SDK transport, `--auth auto` delegates credential selection to the runtime, which supports environment tokens, saved Copilot logins, and GitHub CLI fallback. `--auth copilot-cli` clears token variables in the child process and requires a saved Copilot user login. Without an explicit `--account`, the utility does not read the OS credential store in these modes.

With `--transport http`, `--auth auto` uses:

1. `COPILOT_GITHUB_TOKEN`, then `GH_TOKEN`, then `GITHUB_TOKEN` if present.
2. The selected Copilot CLI login in `~/.copilot/config.json` and its OS credential store.
3. `gh auth token --hostname github.com`, captured privately in memory.

**You do not need to create, copy, or manage a PAT.** Explicit `--auth gh`, `--auth env`, and named `--account` selections use the existing credential resolver and pass the result privately to the SDK. This works around a 1.0.34 account-list schema mismatch with mixed GitHub CLI/Copilot logins. The utility does not create its own credential file.

Useful controls:

```powershell
dotnet run --file Decision.cs -- login
dotnet run --file Decision.cs -- login --auth gh
dotnet run --file Decision.cs -- models --auth copilot-cli
dotnet run --file Decision.cs -- models --auth gh
dotnet run --file Decision.cs -- models --auth copilot-cli --account YOUR_LOGIN
```

`--auth copilot-cli` and `--auth gh` explicitly use stored logins, ignoring token environment variables. `--account` also skips environment-token selection in `auto` mode. A named account's resolved credential is bound to the SDK client used for both discovery and inference. Use `--auth gh --account LOGIN` for a GitHub CLI account. Failures are reported without switching identities or transports.

`--copilot-config PATH` selects another configuration location. For SDK mode, the file must be named `config.json`; its directory becomes the SDK's base directory. In HTTP mode, the custom Windows Credential Manager reader was verified; macOS/Linux readers remain unverified. Named account selection and the HTTP login reader currently target **github.com**.

### SDK runtime selection

The default is the runtime bundled by the pinned NuGet package. To select a compatible existing CLI explicitly, use `--copilot-cli-path PATH`. The SDK checks protocol compatibility; arbitrary newer CLIs may not match SDK 1.0.34.

To build without acquiring bundled runtime assets and use an existing **1.0.112** installation:

```powershell
dotnet build Decision.cs -p:CopilotSkipCliDownload=true
dotnet run --no-build --file Decision.cs -- mcp --copilot-cli-path (Get-Command copilot).Source
```

An HTTP-only deployment can also omit the runtime assets with that build property and select `--transport http`. The normal build/publish path includes the matching assets automatically.

### Foundry: use `az login`

Deploy Microsoft-Decision-1 or GPT-6 Luna in your Azure resource first. Supply the **resource root** and the **deployment name**, which can differ from the model name.

```powershell
az login

dotnet run --file Decision.cs -- decide --provider foundry --endpoint https://YOUR-RESOURCE.services.ai.azure.com --model YOUR-DECISION-DEPLOYMENT --protocol systemone --auth azure-cli --input examples/ticket.json

dotnet run --file Decision.cs -- decide --provider foundry --endpoint https://YOUR-RESOURCE.openai.azure.com --model YOUR-LUNA-DEPLOYMENT --protocol decisions --auth azure-cli --input examples/ticket.json
```

`--auth azure-cli` uses `AzureCliCredential`. `--auth entra` uses `DefaultAzureCredential`, including its managed-identity/developer credential sources. Both request `https://cognitiveservices.azure.com/.default`.

For `--auth api-key`, supply `AZURE_API_KEY` or `AZURE_OPENAI_API_KEY`; `--api-key-env NAME` selects another environment variable. Foundry `auto` prefers a configured key and otherwise uses `DefaultAzureCredential`. Keys use the `api-key` header; Entra tokens use `Authorization: Bearer`.

Foundry use is billed to Azure, independently of Copilot. Regional Decisions availability matters; consult the linked Microsoft documentation. The Foundry adapters are contract-tested, but no Azure deployment was supplied for a live test.

### Direct OpenAI

Set `OPENAI_API_KEY` through your usual environment/secret-management setup, then:

```powershell
dotnet run --file Decision.cs -- decide --provider openai --model gpt-6-luna --input examples/ticket.json
```

This uses your OpenAI API billing, independently of Copilot. This adapter shares the Decisions protocol implementation; it was not live-tested against OpenAI directly.

## Configuration profiles

Copy `decision.config.example.json` to `decision.config.json` and replace the Foundry resource/deployment placeholders as needed. Pass the file explicitly:

```powershell
dotnet run --file Decision.cs -- decide --config decision.config.json --profile luna --input examples/ticket.json
dotnet run --file Decision.cs -- mcp --config decision.config.json --profile foundry-luna
```

Precedence: **CLI options > environment > selected profile > built-in defaults**. `defaultProfile` selects a profile when `--profile` is omitted. Configuration contains environment-variable **names**, not secret values.

| CLI option | Environment variable | Profile property |
| --- | --- | --- |
| `--config` | `DECISION_CONFIG` | - |
| `--profile` | `DECISION_PROFILE` | `defaultProfile` at the config root |
| `--provider` | `DECISION_PROVIDER` | `provider` |
| `--transport` | `DECISION_TRANSPORT` | `transport` |
| `--copilot-cli-path` | `DECISION_COPILOT_CLI_PATH` | `copilotCliPath` |
| `--model` / `--default-model` | `DECISION_MODEL` | `model` |
| `--endpoint` | `DECISION_ENDPOINT` | `endpoint` |
| `--protocol` | `DECISION_PROTOCOL` | `protocol` |
| `--auth` | `DECISION_AUTH` | `auth` |
| `--account` | `DECISION_ACCOUNT` | `account` |
| `--copilot-config` | `DECISION_COPILOT_CONFIG` | `copilotConfig` |
| `--api-key-env` | `DECISION_API_KEY_ENV` | `apiKeyEnv` |
| `--timeout` | `DECISION_TIMEOUT_SECONDS` | `timeoutSeconds` |

`transport: "auto"` selects SDK for Copilot and HTTP for Foundry/OpenAI. The example profiles include an explicit `copilot-http` profile. The SDK owns its endpoints and protocol; to use `--endpoint` or `--protocol` with Copilot, select `--transport http`. HTTP endpoints must be HTTPS resource roots, without API paths or query strings. Copilot HTTP endpoint overrides must remain under `githubcopilot.com`; its default root is `https://api.githubcopilot.com`.

## MCP

The app uses the official MCP C# SDK over **stdio**. Start it with the same provider options as the CLI:

```powershell
dotnet build Decision.cs

# Out of the box: microsoft-decision-1 (currently served as microsoft-decision-1.1).
dotnet run --no-build --file Decision.cs -- mcp --auth copilot-cli

# Optionally select a different default for this server.
dotnet run --no-build --file Decision.cs -- mcp --auth copilot-cli --default-model gpt-6-luna-decisions
```

The MCP tool's `model` argument is **optional**, including in its published schema. Model selection is:

1. The tool call's `model`, when supplied.
2. The configured startup default (`--default-model`, its existing alias `--model`, `DECISION_MODEL`, or the profile's `model`).
3. The provider's built-in default: **`microsoft-decision-1` for Copilot** or `gpt-6-luna` for OpenAI. Foundry requires a configured deployment name.

Use only one of `--default-model` and `--model` in a command. Copilot's valid catalog ID is `microsoft-decision-1`; `microsoft-decision-1.1` is the currently returned backend version, not a separately advertised model ID.

There are two tools:

- **`decision_models`**: discover Copilot decision models and SDK limits, including the separate application `defaultModel` and each model's `isRuntimeDefault`; for Foundry/OpenAI, describe the configured deployment/model.
- **`decision_decide`**: arguments are `request` (the common JSON object above) and optional `model`. The provider is fixed by server startup configuration. A model override selects another model within that provider and, for Copilot, discovers its protocol automatically.

Example MCP tool arguments using the server's default model:

```json
{
  "request": {
    "state": "Please refund my payment.",
    "questions": {
      "refund": {
        "type": "predicate",
        "instructions": "Does the customer request a refund?"
      }
    }
  }
}
```

To override the default for one call, add `"model": "gpt-6-luna-decisions"` alongside `request`.

`mcp.example.json` contains a `mcpServers` entry using this workspace's absolute path. Adapt the enclosing configuration format to your MCP client. Build once before using `--no-build`; this keeps build output out of the MCP stream. All server logging goes to stderr, and successful tools return structured content.

The MCP server lazily starts **one SDK client**, reuses it across tool calls, and disposes it when the server stops. The resolved application model is always passed explicitly, so the runtime's current Luna default cannot replace the requested Microsoft default.

For deployment, publish and point the MCP client directly at the executable:

```powershell
dotnet publish Decision.cs -c Release -o artifacts
.\artifacts\Decision.exe models
```

Example published-server entry on Windows:

```json
{
  "mcpServers": {
    "decisions": {
      "command": "W:/Playground/GHCP-Decision/artifacts/Decision.exe",
      "args": ["mcp", "--auth", "copilot-cli"]
    }
  }
}
```

On Linux/macOS, use the published `Decision` executable. Publish on the target platform or specify its RID to acquire matching Copilot assets. Distribute the complete publish directory, including `runtimes/`. Publishing is managed, with Native AOT disabled because this app uses reflection-based MCP/JSON metadata. File-based source and a single-file published executable are separate concepts.

## Implementation and verification

Everything needed by the application is in `Decision.cs`:

- `IDecisionProvider`: hosting/authentication abstraction.
- `CopilotSdkDecisionProvider`: default Copilot implementation, sharing one runtime for both models.
- `CopilotDecisionProvider`: explicit Copilot HTTP implementation. `FoundryDecisionProvider` and `OpenAiDecisionProvider` use HTTP.
- `IDecisionProtocol`, `SystemOneProtocol`, `DecisionsProtocol`: shared HTTP request/response adapters.
- C# 15 **closed record hierarchies**, exhaustive switches, collection-constructor arguments, and a labeled loop exit.
- `DecisionService`: shared CLI/MCP execution, cancellation, and timeout handling.
- Catalog caching for five minutes in the MCP process. HTTP catalog entries are invalidated when the credential changes; SDK credential management belongs to the runtime. Advertised models and SDK request limits are checked before inference. Hidden decision models remain discoverable.

Run the offline contract and MCP integration tests (also a file-based app):

```powershell
dotnet build Decision.cs
dotnet run --file tests/Decision.Tests.cs
```

Tests use recorded HTTP responses and an SDK-shaped fixture that makes confidence differences explicit. They cover protocol mapping, defaults/overrides, shared runtime lifecycle, limits, account selection, malformed results, refusals, credential redaction, cancellation, and MCP initialization/schema/tool calls.

To additionally make two live MCP inference calls using the saved Copilot CLI login:

```powershell
dotnet run --file tests/Decision.Tests.cs -- --live
```

HTTP errors and SDK RPC failures are reported explicitly, including supplied retry delays. The utility invokes inference once per request and does not retry or switch transports/providers/models automatically.
