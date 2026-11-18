#!/usr/bin/env -S dotnet --
#:property TargetFramework=net11.0
#:property PublishAot=false
#:package GitHub.Copilot.SDK@1.0.34

using GitHub.Copilot;
using GitHub.Copilot.Rpc;

#pragma warning disable GHCP001 // Opt in to the SDK's experimental Decisions API.

// 1. Enable decisions and start the runtime using your saved Copilot login.
Environment.SetEnvironmentVariable("COPILOT_CLI_ENABLED_FEATURE_FLAGS", "DECISIONS_API");

await using var client = new CopilotClient();
await client.StartAsync();

string model = args.FirstOrDefault() ?? "microsoft-decision-1";

// 2. Send the evidence and three named questions in one request.
var result = await client.Rpc.Decisions.AskAsync(
    context: new DecisionContextText
    {
        Text = "I was charged twice for my order. Please refund the duplicate charge."
    },
    questions:
    [
        new DecisionQuestionBoolean
        {
            Name = "refund",
            Instructions = "Does the customer request a refund?"
        },
        new DecisionQuestionChoice
        {
            Name = "team",
            Instructions = "Which team should handle this ticket?",
            Options =
            [
                new DecisionOption { Value = "billing", Description = "Payments, invoices, and refunds" },
                new DecisionOption { Value = "engineering", Description = "Software bugs and outages" },
                new DecisionOption { Value = "support", Description = "Other requests" }
            ]
        },
        new DecisionQuestionScore
        {
            Name = "severity",
            Instructions = "How severe is this ticket?",
            Levels = // Ordered from lowest to highest.
            [
                new DecisionLevel { Label = "Low", Description = "Minor inconvenience" },
                new DecisionLevel { Label = "Medium", Description = "Significant issue affecting one customer" },
                new DecisionLevel { Label = "High", Description = "Critical issue affecting many customers" }
            ]
        }
    ],
    model: model,
    timeoutMs: 30_000);

// 3. Read the typed answers. Boolean questions return a probability of yes.
Console.WriteLine($"Model: {result.UpstreamModel}");

foreach (var answer in result.Answers)
{
    Console.WriteLine(answer switch
    {
        DecisionAnswerBoolean boolean => $"{boolean.Name}: {boolean.Probability:P0} probability of yes",
        DecisionAnswerChoice choice => $"{choice.Name}: {choice.Value}",
        DecisionAnswerScore score => $"{score.Name}: {score.Level} (score: {score.Score:F2})",
        DecisionAnswerRefused refused => $"{refused.Name}: refused",
        _ => throw new InvalidOperationException("Unexpected decision answer type.")
    });
}

#pragma warning restore GHCP001
