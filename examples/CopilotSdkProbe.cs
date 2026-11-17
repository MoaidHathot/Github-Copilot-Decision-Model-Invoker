#!/usr/bin/env -S dotnet --
#:property TargetFramework=net11.0
#:property LangVersion=15.0
#:property PublishAot=false
#:property CopilotSkipCliDownload=true
#:package GitHub.Copilot.SDK@1.0.34

using System.Collections;
using System.Diagnostics;
using System.Text.Json;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;

#pragma warning disable GHCP001 // The sessionless Decisions RPC is experimental.

if (args.Length != 1)
    throw new ArgumentException("Supply the absolute path to a Copilot CLI 1.0.112 executable.");

var environment = Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
    .ToDictionary(e => (string)e.Key, e => (string)e.Value!);
environment["COPILOT_CLI_ENABLED_FEATURE_FLAGS"] = "DECISIONS_API";
foreach (string name in new[] { "COPILOT_GITHUB_TOKEN", "GH_TOKEN", "GITHUB_TOKEN" })
    environment.Remove(name);

using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
var total = Stopwatch.StartNew();
await using var client = new CopilotClient(new CopilotClientOptions
{
    Connection = RuntimeConnection.ForStdio(path: args[0], args: ["--no-auto-update"]),
    Environment = environment,
    UseLoggedInUser = true,
    LogLevel = CopilotLogLevel.None
});

await client.StartAsync(timeout.Token);
var status = await client.GetStatusAsync(timeout.Token);
var auth = await client.GetAuthStatusAsync(timeout.Token);
Console.WriteLine(JsonSerializer.Serialize(new
{
    sdk = "GitHub.Copilot.SDK 1.0.34",
    status.Version,
    status.ProtocolVersion,
    auth.IsAuthenticated,
    auth.AuthType,
    auth.Login,
    startupMilliseconds = total.Elapsed.TotalMilliseconds
}));
if (status.Version != "1.0.112" || status.ProtocolVersion != 3)
    throw new InvalidOperationException("This probe requires the pinned 1.0.112 / protocol-3 runtime.");

var catalog = await client.Rpc.Models.ListAsync(cancellationToken: timeout.Token);
Console.WriteLine(JsonSerializer.Serialize(catalog.DecisionModels, new JsonSerializerOptions { WriteIndented = true }));

foreach (string model in new[] { "microsoft-decision-1", "gpt-6-luna-decisions" })
{
    var watch = Stopwatch.StartNew();
    var result = await client.Rpc.Decisions.AskAsync(
        context: new DecisionContextText { Text = "I was charged twice for my order. Please refund the duplicate charge." },
        questions:
        [
            new DecisionQuestionBoolean { Name = "refund", Instructions = "Does the customer request a refund?" },
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
                Levels =
                [
                    new DecisionLevel { Label = "Low", Description = "Minor issue" },
                    new DecisionLevel { Label = "Medium", Description = "Significant issue" },
                    new DecisionLevel { Label = "High", Description = "Critical issue" }
                ]
            }
        ],
        model: model,
        timeoutMs: 30000,
        cancellationToken: timeout.Token);

    Console.WriteLine(JsonSerializer.Serialize(new
    {
        result.Model,
        result.UpstreamModel,
        result.LatencyMs,
        elapsedMilliseconds = watch.Elapsed.TotalMilliseconds,
        result.Usage,
        answers = result.Answers.Select(answer => answer switch
        {
            DecisionAnswerBoolean b => (object)new { kind = "boolean", b.Name, b.Probability },
            DecisionAnswerChoice c => new { kind = "choice", c.Name, c.Value, c.Probabilities, c.Confidence, c.VendorConfidence, c.Tied },
            DecisionAnswerScore s => new { kind = "score", s.Name, s.Score, s.Level, s.Probabilities, s.Confidence, s.VendorConfidence },
            DecisionAnswerRefused r => new { kind = "refused", r.Name },
            _ => throw new InvalidOperationException("Unexpected decision answer type.")
        }).ToArray()
    }, new JsonSerializerOptions { WriteIndented = true }));
}

await client.StopAsync();
