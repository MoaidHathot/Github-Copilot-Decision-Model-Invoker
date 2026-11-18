# Minimal Copilot decision sample

[Decision.cs](Decision.cs) is a standalone, file-based C# app using **GitHub.Copilot.SDK 1.0.34**. It starts a client, sends one support ticket with three questions, and prints the typed answers.

## Run

You need the [.NET 11 SDK](https://dotnet.microsoft.com/download/dotnet/11.0) and a GitHub Copilot account with access to decision models. Sign in once with the [Copilot CLI](https://github.com/github/copilot-cli):

```powershell
copilot login
```

From the repository root:

```powershell
dotnet run --file sample/Decision.cs

# Run the same request with another decision model.
dotnet run --file sample/Decision.cs -- gpt-6-luna-decisions
```

The default model is `microsoft-decision-1`. The first build downloads the SDK's matching runtime; the client reuses your saved login. The code enables `DECISIONS_API` in the sample process's environment so the child runtime inherits it. `GHCP001` is suppressed because the SDK marks this API experimental.

## Demo walkthrough

1. **Start:** `CopilotClient` starts the bundled runtime. `await using` disposes it when the program exits.
2. **Ask:** `client.Rpc.Decisions.AskAsync` accepts evidence (`context`), named questions (`questions`), and a model ID (`model`). This is a sessionless call.
3. **Read:** Each question produces a typed answer with the same name:

| Question | SDK type | Result |
| --- | --- | --- |
| Is a refund requested? | `DecisionQuestionBoolean` | Probability of yes, from 0 to 1. |
| Which team should handle it? | `DecisionQuestionChoice` | One of the supplied option values, such as `billing`. |
| How severe is it? | `DecisionQuestionScore` | The most probable level and a probability-weighted score over zero-based level positions (0–2 here); the score can be fractional. |

The result can also contain `DecisionAnswerRefused`. Choice and score answers expose `Probabilities` if you want to show the full distribution. Change the ticket text, instructions, options, or levels to try different decisions.

To see model IDs available to your account, add this after `StartAsync()`:

```csharp
var catalog = await client.Rpc.Models.ListAsync();
foreach (var availableModel in catalog.DecisionModels ?? [])
    Console.WriteLine(availableModel.Id);
```
