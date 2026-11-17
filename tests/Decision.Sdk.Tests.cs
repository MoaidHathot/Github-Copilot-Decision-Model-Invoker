using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using CopilotSdk = GitHub.Copilot;
using CopilotRpc = GitHub.Copilot.Rpc;

#pragma warning disable GHCP001

public static partial class DecisionTests
{
    private static async Task SdkProvider()
    {
        var runtime = new TestSdkRuntime();
        var options = new ProviderOptions { Model = "gpt-6-luna-decisions" };
        using var http = new HttpClient(new MockHandler((_, _) => throw new Exception("SDK calls must not use our HTTP client")));
        var provider = new CopilotSdkDecisionProvider(options, runtime);
        var service = new DecisionService(options, http, provider);
        try
        {
            var results = await Task.WhenAll(
                service.DecideAsync(Ticket, null, default),
                service.DecideAsync(Ticket, "Microsoft-Decision-1", default));
            Equal(1, runtime.Starts);
            Equal(1, runtime.Lists);
            Equal(2, runtime.Calls.Count);
            Equal("gpt-6-luna-decisions", results[0].RequestedModel);
            Equal("microsoft-decision-1", results[1].RequestedModel);
            Equal("microsoft-decision-1.1", results[1].Model);
            Equal("sdk", results[0].Transport);
            Equal("decisions.ask", results[0].Protocol);
            Equal("copilot-sdk-json", results[0].RawFormat);
            Equal("choice", results[0].Raw.GetProperty("answers")[1].GetProperty("kind").GetString());
            var choice = (ChoiceAnswer)results[0].Answers["team"];
            Equal("billing", choice.Choice);
            Equal(0.65, choice.Confidence);
            Equal(0.7, choice.NormalizedConfidence);
            Equal(false, choice.Tied);
            Equal(0.8, choice.Probabilities["billing"]);
            var score = (ScoreAnswer)results[0].Answers["severity"];
            Equal(1.4, score.Score);
            Equal(0.3, score.Confidence);
            Equal(0.25, score.NormalizedConfidence);
            Equal(0.4, score.Probabilities[1]);
            Equal(100, results[0].Usage!.Value.GetProperty("input_tokens").GetInt32());
            Equal(20, results[0].Usage!.Value.GetProperty("input_tokens_details").GetProperty("cached_tokens").GetInt32());
            Check(!results[0].Usage!.Value.TryGetProperty("total_tokens", out _), "SDK usage must not invent unreported totals");
            Check(runtime.Calls.All(c => c.TimeoutMs == 30000 && c.SelectionId == "test-selection"), "SDK deadline and account propagate to every call");
            var catalog = await service.ModelsAsync(default);
            Equal("sdk", catalog.Transport);
            Equal("gpt-6-luna-decisions", catalog.DefaultModel);
            Equal(32L, catalog.Models[0].Limits!.MaxQuestions);
            Equal(10L, catalog.Models[0].Limits!.MaxChoices);
            Check(catalog.Models.All(m => m.SupportedEndpoints.Length == 0), "SDK catalog must not invent HTTP endpoints");
            Check(catalog.Models.Single(m => m.Id == "gpt-6-luna-decisions").IsRuntimeDefault == true, "expose the distinct runtime default");
            Equal(1, runtime.Lists);
        }
        finally { await service.DisposeAsync(); }
        await service.DisposeAsync();
        Equal(1, runtime.Disposals);

        var defaultRuntime = new TestSdkRuntime();
        await using var defaults = new CopilotSdkDecisionProvider(new(), defaultRuntime);
        var microsoft = await defaults.DecideAsync(Ticket, null, default);
        Equal("microsoft-decision-1", microsoft.RequestedModel);
        Equal("microsoft-decision-1", defaultRuntime.Calls.Single().Model);

        var response = SdkFixture();
        response.Answers[0] = new CopilotRpc.DecisionAnswerRefused { Name = "refund" };
        ((CopilotRpc.DecisionAnswerChoice)response.Answers[1]).VendorConfidence = null;
        var answers = SdkMapping.ReadAnswers(response, Ticket);
        Check(answers["refund"] is RefusalAnswer, "SDK refusals must not become negative decisions");
        Check(((ChoiceAnswer)answers["team"]).Confidence is null, "absent vendor confidence must not be replaced with SDK confidence");
        Equal(0.7, ((ChoiceAnswer)answers["team"]).NormalizedConfidence);
        ((CopilotRpc.DecisionAnswerChoice)response.Answers[1]).Name = "refund";
        Throws(() => SdkMapping.ReadAnswers(response, Ticket), "question names");
        response = SdkFixture();
        ((CopilotRpc.DecisionAnswerScore)response.Answers[2]).Probabilities[0].Label = "Wrong level";
        Throws(() => SdkMapping.ReadAnswers(response, Ticket), "request order");
    }

    private static async Task SdkConstraints()
    {
        var runtime = new TestSdkRuntime();
        await using var provider = new CopilotSdkDecisionProvider(new(), runtime);
        await ThrowsAsync(() => provider.DecideAsync(Ticket with { Images = ["https://example.com/image.png"] }, null, default), "--transport http");
        Equal(0, runtime.Starts);
        var manyQuestions = Ticket with { Questions = Enumerable.Range(0, 33).ToDictionary(i => $"q{i}", _ => (DecisionQuestion)new PredicateQuestion("True?")) };
        await ThrowsAsync(() => provider.DecideAsync(manyQuestions, null, default), "32 questions");
        var choices = Enumerable.Range(0, 11).ToDictionary(i => $"c{i}", i => $"Choice {i}");
        var manyChoices = Ticket with { Questions = new() { ["q"] = new ChoiceQuestion("Choose", choices) } };
        await ThrowsAsync(() => provider.DecideAsync(manyChoices, null, default), "2-10 choices");
        var spacedChoice = Ticket with { Questions = new() { ["q"] = new ChoiceQuestion("Choose", new() { ["has space"] = "A", ["ok"] = "B" }) } };
        await ThrowsAsync(() => provider.DecideAsync(spacedChoice, null, default), "ASCII");
        await ThrowsAsync(() => provider.DecideAsync(Ticket with { State = JsonSerializer.SerializeToElement(new string('x', 262144)) }, null, default), "serialized bytes");
        JsonNode deep = new JsonObject();
        for (int i = 0; i < 33; i++) deep = new JsonObject { ["child"] = deep };
        await ThrowsAsync(() => provider.DecideAsync(Ticket with { State = JsonSerializer.SerializeToElement(deep) }, null, default), "nesting limit");
        await ThrowsAsync(() => provider.DecideAsync(Ticket, "gpt-6-luna", default), "not advertised");
        Equal(0, runtime.Calls.Count);

        var structured = Ticket with { State = JsonSerializer.SerializeToElement(new { role = "system", detail = "ordinary evidence" }) };
        var call = SdkMapping.CreateCall(structured, "microsoft-decision-1", 30000, "account");
        Check(call.Context is CopilotRpc.DecisionContextJson, "SDK object evidence uses JSON context");
        Check(JsonElement.DeepEquals(structured.State, ((CopilotRpc.DecisionContextJson)call.Context).Value), "SDK JSON evidence is not reinterpreted");
        call = SdkMapping.CreateCall(Ticket with { State = JsonSerializer.SerializeToElement(true) }, "model", 1000, null);
        Equal("true", ((CopilotRpc.DecisionContextText)call.Context).Text);
        call = SdkMapping.CreateCall(Ticket, "model", 1000, null);
        Check(call.Questions[0] is CopilotRpc.DecisionQuestionBoolean { Name: "refund" }, "predicate maps to SDK boolean");
        Equal("Payments, invoices, and refunds", ((CopilotRpc.DecisionQuestionChoice)call.Questions[1]).Options[0].Description);
        Equal("High", ((CopilotRpc.DecisionQuestionScore)call.Questions[2]).Levels[2].Description);

        runtime.OnAsk = (_, _) => throw new IOException("unsafe fixture-secret", new CopilotSdk.RemoteRpcException(
            "unsafe fixture-secret", -32000, JsonSerializer.SerializeToElement(new { kind = "rateLimited", retryAfterMs = 2500 })));
        var error = await ThrowsAsync(() => provider.DecideAsync(Ticket, null, default), "2500 ms");
        Check(!error.Message.Contains("fixture-secret", StringComparison.Ordinal), "SDK exception bodies must not leak credentials");
        Equal(1, runtime.Calls.Count);
        runtime.OnAsk = async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); throw new UnreachableException(); };
        using var cancelled = new CancellationTokenSource(30);
        try { await provider.DecideAsync(Ticket, null, cancelled.Token); throw new Exception("Expected SDK cancellation"); }
        catch (OperationCanceledException) { assertions++; }
        Equal(1, runtime.Starts);
        Equal(2, runtime.Calls.Count);

        using var env = new EnvironmentScope("COPILOT_CLI_ENABLED_FEATURE_FLAGS", "COPILOT_GITHUB_TOKEN", "GH_TOKEN", "GITHUB_TOKEN");
        Environment.SetEnvironmentVariable("COPILOT_CLI_ENABLED_FEATURE_FLAGS", "parent-flag");
        Environment.SetEnvironmentVariable("GH_TOKEN", "fixture-secret");
        var child = CopilotDecisionRuntime.ChildEnvironment(new() { Auth = "copilot-cli" });
        Equal("DECISIONS_API", child["COPILOT_CLI_ENABLED_FEATURE_FLAGS"]);
        Check(!child.ContainsKey("GH_TOKEN"), "saved-login SDK mode excludes environment tokens");
        Equal("parent-flag", Environment.GetEnvironmentVariable("COPILOT_CLI_ENABLED_FEATURE_FLAGS"));
        Equal("fixture-secret", Environment.GetEnvironmentVariable("GH_TOKEN"));
        Check(CopilotDecisionRuntime.ChildEnvironment(new()).ContainsKey("GH_TOKEN"), "auto authentication preserves runtime credential precedence");
        Check(!CopilotDecisionRuntime.ChildEnvironment(new() { Account = "named-user" }).ContainsKey("GH_TOKEN"), "an unrelated environment token cannot override a named account");
        var schemaError = SdkMapping.Error(new JsonException("fixture-secret must not be echoed"));
        Check(!schemaError.Message.Contains("fixture-secret", StringComparison.Ordinal), "SDK schema failures must not expose arbitrary response text");
        Throws(() => new ProviderOptions { Endpoint = "https://api.githubcopilot.com" }.Validate(), "--transport http");
        Throws(() => new ProviderOptions { Provider = "openai", Transport = "sdk" }.Validate(), "Only Copilot");
        Equal("sdk", new ProviderOptions().EffectiveTransport);
        Equal("http", new ProviderOptions { Provider = "foundry" }.EffectiveTransport);
    }

    private static CopilotRpc.DecisionsAskResult SdkFixture() => JsonSerializer.Deserialize<CopilotRpc.DecisionsAskResult>(
        File.ReadAllText(Path.Combine(Root, "tests", "fixtures", "sdk.response.json")), JsonData.Options)!;

    private sealed class TestSdkRuntime : ICopilotDecisionRuntime
    {
        public int Starts;
        public int Lists;
        public int Disposals;
        public string CredentialSource => "test-sdk-user";
        public string? SelectionId => "test-selection";
        public ConcurrentQueue<SdkDecisionCall> Calls { get; } = new();
        public Func<SdkDecisionCall, CancellationToken, Task<CopilotRpc.DecisionsAskResult>>? OnAsk { get; set; }
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Starts);
            await Task.Delay(20, cancellationToken);
        }
        public Task<IReadOnlyList<CopilotRpc.DecisionModelInfo>> ListModelsAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Lists);
            return Task.FromResult<IReadOnlyList<CopilotRpc.DecisionModelInfo>>(
                new[] { "microsoft-decision-1", "gpt-6-luna-decisions" }.Select(id => new CopilotRpc.DecisionModelInfo
                {
                    Id = id, Name = id, IsDefault = id.StartsWith("gpt-", StringComparison.Ordinal),
                    Limits = new()
                    {
                        MaxQuestions = 32, MinOptions = 2, MaxOptions = 10, MinLevels = 2, MaxLevels = 10,
                        MaxRequestBytes = 262144, MaxJsonDepth = 32, ContextPlusQuestionTokens = id.StartsWith("microsoft-", StringComparison.Ordinal) ? 32768 : null
                    }
                }).ToArray());
        }
        public Task<CopilotRpc.DecisionsAskResult> AskAsync(SdkDecisionCall call, CancellationToken cancellationToken)
        {
            Calls.Enqueue(call);
            if (OnAsk is not null) return OnAsk(call, cancellationToken);
            var result = SdkFixture();
            result.Model = call.Model;
            result.UpstreamModel = call.Model == "microsoft-decision-1" ? "microsoft-decision-1.1" : "gpt-6-luna";
            return Task.FromResult(result);
        }
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }
}
