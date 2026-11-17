#!/usr/bin/env -S dotnet --
#:include ../Decision.cs
#:property StartupObject=DecisionTests
#:property CopilotSkipCliDownload=true
#:property IsPackable=false
#:include Decision.Sdk.Tests.cs

using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;

// File-based contract tests: no test project and no live inference unless --live is supplied.
public static partial class DecisionTests
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(SourceDirectory(), ".."));
    private static readonly DecisionRequest Ticket = JsonData.ReadRequest(File.ReadAllText(Path.Combine(Root, "examples", "ticket.json")));
    private static int assertions;

    public static async Task<int> Main(string[] args)
    {
        (string Name, Func<Task> Test)[] tests =
        [
            ("Public request and wire contracts", Payloads),
            ("Recorded provider responses and confidence", Answers),
            ("Partial refusals and answer-name matching", Refusals),
            ("Reject malformed provider responses", InvalidResponses),
            ("Request validation and JSON ambiguity", Validation),
            ("Copilot hidden-model discovery, routes, and cache", CopilotRoutes),
            ("Copilot policy and incompatible models", CopilotPolicies),
            ("Foundry deployment paths and authentication headers", FoundryRoutes),
            ("HTTP error redaction and rate limits", HttpErrors),
            ("Timeout and caller cancellation", Cancellation),
            ("Configuration precedence and CLI validation", ConfigurationAndCli),
            ("Environment credential handling", EnvironmentCredentials),
            ("SDK lifecycle, defaults, and normalized results", SdkProvider),
            ("SDK limits, errors, and authentication selection", SdkConstraints),
            ("MCP stdio schema, structured result, and validation", () => Mcp(live: false))
        ];
        try
        {
            foreach (var (name, test) in tests)
            {
                await test();
                Console.WriteLine($"PASS {name}");
            }
            if (args.Contains("--live", StringComparer.Ordinal))
            {
                await Mcp(live: true);
                Console.WriteLine("PASS live Copilot MCP calls: Microsoft-Decision-1 and GPT-6 Luna Decisions");
            }
            Console.WriteLine($"{tests.Length} offline groups passed; {assertions} assertions.");
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"FAIL {e}");
            return 1;
        }
    }

    private static Task Payloads()
    {
        var systemOne = new SystemOneProtocol().CreatePayload("microsoft-decision-1", Ticket);
        Equal("microsoft-decision-1", systemOne["model"]!.GetValue<string>());
        Equal("noul", systemOne["questions"]!["refund"]!["type"]!.GetValue<string>());
        Equal("Payments, invoices, and refunds", systemOne["questions"]!["team"]!["criteria"]!["billing"]!.GetValue<string>());
        Equal(3, systemOne["questions"]!["severity"]!["criteria"]!.AsArray().Count);
        Check(systemOne["input"] is null, "System One must use state, not input");

        var decisions = new DecisionsProtocol().CreatePayload("gpt-6-luna-decisions", Ticket);
        Equal("gpt-6-luna-decisions", decisions["model"]!.GetValue<string>());
        Equal("refund", decisions["questions"]![0]!["name"]!.GetValue<string>());
        Equal("predicate", decisions["questions"]![0]!["type"]!.GetValue<string>());
        Equal("billing", decisions["questions"]![1]!["choices"]![0]!["value"]!.GetValue<string>());
        Equal("High", decisions["questions"]![2]!["levels"]![2]!["label"]!.GetValue<string>());
        Check(decisions["state"] is null, "Decisions must use input, not state");

        var evidence = JsonSerializer.SerializeToElement(new { role = "system", image_url = "https://example.com/ordinary-data", count = 2 });
        var structured = Ticket with { State = evidence };
        var text = new DecisionsProtocol().CreatePayload("luna", structured)["input"]!.GetValue<string>();
        Check(JsonElement.DeepEquals(evidence, JsonDocument.Parse(text).RootElement), "JSON evidence must survive text encoding");
        Check(JsonElement.DeepEquals(evidence, JsonSerializer.SerializeToElement(new SystemOneProtocol().CreatePayload("ms", structured)["state"])), "System One preserves JSON evidence");

        var images = Ticket with { Images = ["https://example.com/product.png"] };
        images.Validate();
        var content = new DecisionsProtocol().CreatePayload("luna", images)["input"]![0]!["content"]!.AsArray();
        Equal("input_text", content[0]!["type"]!.GetValue<string>());
        Equal("input_image", content[1]!["type"]!.GetValue<string>());
        Throws(() => new SystemOneProtocol().CreatePayload("ms", images), "text-only");
        var roundTrip = JsonData.ReadRequest(JsonSerializer.Serialize(Ticket, JsonData.Options));
        Check(roundTrip.Questions["team"] is ChoiceQuestion, "closed hierarchy JSON round trip");
        return Task.CompletedTask;
    }

    private static Task Answers()
    {
        var ms = new SystemOneProtocol().ReadAnswers(Fixture("systemone.response.json"), Ticket);
        Equal("billing", ((ChoiceAnswer)ms["team"]).Choice);
        Equal(0.9984835579242881, ((ChoiceAnswer)ms["team"]).Confidence);
        Equal(0.998989038616192, ((ChoiceAnswer)ms["team"]).Probabilities["billing"]);
        Equal(0.9995788684419998, ((PredicateAnswer)ms["refund"]).Probability);
        Equal(1.387337633097034, ((ScoreAnswer)ms["severity"]).Score);
        Equal("Low", ((ScoreAnswer)ms["severity"]).Levels[0]);

        var openai = new DecisionsProtocol().ReadAnswers(Fixture("decisions.response.json"), Ticket);
        Equal("billing", ((ChoiceAnswer)openai["team"]).Choice);
        Equal(0.8, ((ScoreAnswer)openai["severity"]).Score);
        Equal(0.67, ((ScoreAnswer)openai["severity"]).Confidence);
        Equal(0.78, ((ScoreAnswer)openai["severity"]).Probabilities[1]);
        var reordered = FixtureNode("decisions.response.json");
        var array = reordered["answers"]!.AsArray();
        var first = array[0];
        array.RemoveAt(0);
        array.Add(first);
        var parsed = new DecisionsProtocol().ReadAnswers(JsonSerializer.SerializeToElement(reordered), Ticket);
        Check(parsed["refund"] is PredicateAnswer && parsed["team"] is ChoiceAnswer, "match by name, never position");
        return Task.CompletedTask;
    }

    private static Task Refusals()
    {
        var response = FixtureNode("decisions.response.json");
        response["answers"]![0] = new JsonObject { ["name"] = "refund", ["type"] = "refusal" };
        var answers = new DecisionsProtocol().ReadAnswers(JsonSerializer.SerializeToElement(response), Ticket);
        Check(answers["refund"] is RefusalAnswer, "refusal must not become a zero probability");
        Check(answers["team"] is ChoiceAnswer, "other answers survive a partial refusal");
        return Task.CompletedTask;
    }

    private static Task InvalidResponses()
    {
        foreach (Action<JsonNode> corrupt in new Action<JsonNode>[]
        {
            n => n["answers"]!.AsArray().RemoveAt(0),
            n => n["answers"]![0]!["name"] = "team",
            n => n["answers"]![0]!["probability"] = 1.5,
            n => n["answers"]![1]!["choice"] = "not-supplied",
            n => n["answers"]![1]!["probabilities"]!.AsArray().RemoveAt(0),
            n => n["answers"]![1]!["probabilities"]![0]!["value"] = "engineering",
            n => n["answers"]![2]!["score"] = 9,
            n => n["answers"]![2]!["probabilities"]![0]!["value"] = 0.5,
            n => n["answers"]![0]!["type"] = "choice"
        })
        {
            var response = FixtureNode("decisions.response.json");
            corrupt(response);
            Throws(() => new DecisionsProtocol().ReadAnswers(JsonSerializer.SerializeToElement(response), Ticket), "Invalid provider response");
        }
        var ms = FixtureNode("systemone.response.json");
        ms["answers"]!["team"]!["probabilities"]!.AsObject().Remove("support");
        Throws(() => new SystemOneProtocol().ReadAnswers(JsonSerializer.SerializeToElement(ms), Ticket), "supplied choices");
        return Task.CompletedTask;
    }

    private static Task Validation()
    {
        Throws(() => (Ticket with { Questions = [] }).Validate(), "named questions");
        Throws(() => (Ticket with { State = JsonSerializer.SerializeToElement("") }).Validate(), "evidence");
        Throws(() => (Ticket with { Questions = new() { ["q"] = new ChoiceQuestion("Choose", new() { ["only"] = "One" }) } }).Validate(), "2-255");
        Throws(() => (Ticket with { Questions = new() { ["q"] = new ScoreQuestion("Rank", ["Same", "Same"]) } }).Validate(), "distinct levels");
        Throws(() => (Ticket with { Images = ["C:/private/photo.png"] }).Validate(), "HTTPS");
        Throws<JsonException>(() => JsonData.ReadRequest("""{"state":"x","questions":{},"unexpected":true}"""));
        Throws<JsonException>(() => JsonData.ReadRequest("""{"state":"x","state":"y","questions":{}}"""));
        Throws<JsonException>(() => JsonData.ReadRequest("""{"state":"x","questions":{"q":{"type":"choice","instructions":"x"}}}"""));
        Throws<JsonException>(() => JsonData.ReadRequest("""{"state":"x","questions":{"q":{"type":"predicate","instructions":null}}}"""));
        return Task.CompletedTask;
    }

    private static async Task CopilotRoutes()
    {
        var handler = new MockHandler(async (message, ct) =>
        {
            Equal("fixture-token", message.Headers.Authorization?.Parameter);
            Check(message.Headers.Contains("Editor-Version"), "Copilot editor header");
            if (message.Method == HttpMethod.Get) return JsonResponse(Fixture("catalog.json"));
            var body = JsonNode.Parse(await message.Content!.ReadAsStringAsync(ct))!;
            if (message.RequestUri!.AbsolutePath == "/v1/systemone")
            {
                Equal("microsoft-decision-1", body["model"]!.GetValue<string>());
                Check(body["questions"] is JsonObject, "Microsoft question object");
                return JsonResponse(Fixture("systemone.response.json"));
            }
            Equal("/v1/decisions", message.RequestUri.AbsolutePath);
            Equal("gpt-6-luna-decisions", body["model"]!.GetValue<string>());
            Check(body["questions"] is JsonArray, "Luna question array");
            return JsonResponse(Fixture("decisions.response.json"));
        });
        using var http = new HttpClient(handler);
        var credentials = new TestCredentials();
        var provider = new CopilotDecisionProvider(new(), http, credentials);
        var catalog = await provider.ListModelsAsync(default);
        Equal(2, catalog.Models.Length);
        Check(catalog.Models.All(m => m.ModelPickerEnabled == false), "hidden decision models must not be excluded");
        var ms = await provider.DecideAsync(Ticket, null, default);
        Equal("microsoft-decision-1.1", ms.Model);
        Equal("microsoft-decision-1", ms.RequestedModel);
        Equal(95, ms.Usage!.Value.GetProperty("input_tokens").GetInt32());
        var luna = await provider.DecideAsync(Ticket, "GPT-6-luna-Decision", default);
        Equal("gpt-6-luna-decisions", luna.RequestedModel);
        Equal("gpt-6-luna", luna.Model);
        Equal(1, handler.Gets);
        Equal(2, handler.Posts);
        credentials.Source = "same-token-new-source";
        // A changed identity token must invalidate catalog data before reuse.
        credentials.Token = "changed-token";
        handler.Callback = (_, _) => Task.FromResult(JsonResponse(Fixture("catalog.json")));
        await provider.ListModelsAsync(default);
        Equal(2, handler.Gets);
    }

    private static async Task CopilotPolicies()
    {
        var catalog = FixtureNode("catalog.json");
        catalog["data"]![0]!["policy"] = new JsonObject { ["state"] = "disabled" };
        using var http = new HttpClient(new MockHandler((_, _) => Task.FromResult(JsonResponse(JsonSerializer.SerializeToElement(catalog)))));
        var provider = new CopilotDecisionProvider(new(), http, new TestCredentials());
        await ThrowsAsync(() => provider.DecideAsync(Ticket, "microsoft-decision-1", default), "disabled");
        await ThrowsAsync(() => provider.DecideAsync(Ticket, "gpt-6-luna", default), "not advertised");
        var incompatible = new CopilotDecisionProvider(new() { Protocol = "systemone" }, http, new TestCredentials());
        await ThrowsAsync(() => incompatible.DecideAsync(Ticket, "gpt-6-luna-decisions", default), "does not advertise");
        Throws(() => new CopilotDecisionProvider(new() { Endpoint = "https://githubcopilot.com.example.org" }, http), "githubcopilot.com");
    }

    private static async Task FoundryRoutes()
    {
        foreach (string protocol in new[] { "systemone", "decisions" })
        {
            string expected = protocol == "systemone" ? "/providers/microsoft/v1/systemone" : "/openai/v1/decisions";
            using var http = new HttpClient(new MockHandler(async (message, ct) =>
            {
                Equal(expected, message.RequestUri!.AbsolutePath);
                Equal("arbitrary-deployment", JsonNode.Parse(await message.Content!.ReadAsStringAsync(ct))!["model"]!.GetValue<string>());
                Equal("fixture-token", message.Headers.GetValues("api-key").Single());
                Check(message.Headers.Authorization is null && !message.Headers.Contains("Copilot-Integration-Id"), "Foundry headers remain provider-specific");
                return JsonResponse(Fixture(protocol == "systemone" ? "systemone.response.json" : "decisions.response.json"));
            }));
            var options = new ProviderOptions { Provider = "foundry", Protocol = protocol, Model = "arbitrary-deployment", Endpoint = "https://example.services.ai.azure.com" };
            var provider = new FoundryDecisionProvider(options, http, new TestCredentials { Header = "api-key" });
            Equal("arbitrary-deployment", (await provider.DecideAsync(Ticket, null, default)).RequestedModel);
            Check((await provider.ListModelsAsync(default)).Source.Contains("configured", StringComparison.Ordinal), "Foundry must not imply live deployment discovery");
        }
        Throws(() => HttpTransport.ResourceRoot("https://example.com/openai/v1/"), "resource root");
        Throws(() => new ProviderOptions { Provider = "foundry", Endpoint = "https://example.com" }.Validate(), "--protocol");
    }

    private static async Task HttpErrors()
    {
        foreach (string header in new[] { "Authorization", "api-key" })
        {
            using var http = new HttpClient(new MockHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
                { Content = new StringContent("Rejected fixture-token\nplease retry") })));
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.com/v1/decisions");
            new Credential("fixture-token", "test", header).Apply(request);
            var error = await ThrowsAsync(() => HttpTransport.SendAsync(http, request, default), "HTTP 403");
            Check(!error.Message.Contains("fixture-token", StringComparison.Ordinal) && error.Message.Contains("[redacted]", StringComparison.Ordinal), "credentials must be redacted from errors");
        }
        var handler = new MockHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("busy") };
            response.Headers.RetryAfter = new(TimeSpan.FromSeconds(7));
            return Task.FromResult(response);
        });
        using var limitedHttp = new HttpClient(handler);
        using var limitedRequest = new HttpRequestMessage(HttpMethod.Post, "https://example.com/v1/decisions");
        await ThrowsAsync(() => HttpTransport.SendAsync(limitedHttp, limitedRequest, default), "Retry-After: 7");
        Equal(1, handler.Posts);
    }

    private static async Task Cancellation()
    {
        using var http = new HttpClient(new MockHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new UnreachableException();
        }));
        var options = new ProviderOptions { Provider = "openai", TimeoutSeconds = 1 };
        var service = new DecisionService(options, http, new OpenAiDecisionProvider(options, http, new TestCredentials()));
        await ThrowsAsync(() => service.DecideAsync(Ticket, null, default), "timed out");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try { await service.DecideAsync(Ticket, null, cancelled.Token); throw new Exception("Expected cancellation"); }
        catch (OperationCanceledException) { assertions++; }
    }

    private static Task ConfigurationAndCli()
    {
        using var environment = new EnvironmentScope("DECISION_PROVIDER", "DECISION_MODEL", "DECISION_ENDPOINT", "DECISION_AUTH", "DECISION_CONFIG", "DECISION_PROFILE", "DECISION_TIMEOUT_SECONDS", "DECISION_PROTOCOL", "DECISION_TRANSPORT", "DECISION_COPILOT_CLI_PATH");
        string file = Path.Combine(Root, "decision.config.example.json");
        Environment.SetEnvironmentVariable("DECISION_MODEL", "environment-model");
        var options = Configuration.Load(CliArguments.Parse(["models", "--config", file, "--profile", "luna"]));
        Equal("environment-model", options.Model);
        options = Configuration.Load(CliArguments.Parse(["models", "--config", file, "--profile", "luna", "--model", "cli-model"]));
        Equal("cli-model", options.Model);
        Environment.SetEnvironmentVariable("DECISION_MODEL", null);
        options = Configuration.Load(CliArguments.Parse(["models", "--config", file, "--profile", "luna"]));
        Equal("gpt-6-luna-decisions", options.Model);
        Throws(() => CliArguments.Parse(["decide", "--mdoel", "x"]), "Unknown option");
        Throws(() => CliArguments.Parse(["models", "--input", "x"]), "only");
        Throws(() => CliArguments.Parse(["mcp", "--model", "x", "--default-model", "y"]), "more than once");
        Throws(() => Configuration.Load(CliArguments.Parse(["models", "--profile", "missing"])), "requires");
        Throws(() => Configuration.Load(CliArguments.Parse(["models", "--timeout", "0"])), "between 1");
        return Task.CompletedTask;
    }

    private static async Task EnvironmentCredentials()
    {
        using var environment = new EnvironmentScope("COPILOT_GITHUB_TOKEN", "GH_TOKEN", "GITHUB_TOKEN", "AZURE_API_KEY", "AZURE_OPENAI_API_KEY", "CUSTOM_TEST_API_KEY");
        Environment.SetEnvironmentVariable("COPILOT_GITHUB_TOKEN", "gho_fake-not-used-over-network");
        var credential = await new CopilotCredentialSource(new() { Auth = "env" }).GetAsync(default);
        Equal("environment:COPILOT_GITHUB_TOKEN", credential.Source);
        Check(!credential.ToString().Contains("gho_", StringComparison.Ordinal), "credential ToString must not reveal tokens");
        Environment.SetEnvironmentVariable("COPILOT_GITHUB_TOKEN", "ghp_fake-not-used-over-network");
        await ThrowsAsync(async () => { await new CopilotCredentialSource(new() { Auth = "env" }).GetAsync(default); }, "classic PAT");
        Environment.SetEnvironmentVariable("CUSTOM_TEST_API_KEY", "fixture-key");
        credential = await new FoundryCredentialSource(new() { Provider = "foundry", Auth = "api-key", ApiKeyEnv = "CUSTOM_TEST_API_KEY" }).GetAsync(default);
        using var request = new HttpRequestMessage();
        credential.Apply(request);
        Equal("fixture-key", request.Headers.GetValues("api-key").Single());
    }

    private static async Task Mcp(bool live)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(live ? 90 : 30));
        string app = Path.Combine(Root, "Decision.cs");
        string defaultModel = live ? "gpt-6-luna-decisions" : "configured-default-model";
        List<string> args = ["run", "--no-build", "--file", app, "--", "mcp", "--provider", live ? "copilot" : "openai", "--default-model", defaultModel];
        if (live) args.AddRange(["--auth", "copilot-cli"]);
        // Explicit child configuration prevents ambient DECISION_* values changing the test.
        Dictionary<string, string?> env = new(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry variable in Environment.GetEnvironmentVariables())
            if (((string)variable.Key).StartsWith("DECISION_", StringComparison.Ordinal)) env[(string)variable.Key] = null;
        await using var client = await McpClient.CreateAsync(new StdioClientTransport(new()
        {
            Name = "decision-test", Command = "dotnet", Arguments = args, WorkingDirectory = Root, EnvironmentVariables = env
        }), cancellationToken: timeout.Token);
        var tools = await client.ListToolsAsync(cancellationToken: timeout.Token);
        Equal(2, tools.Count);
        var decide = tools.Single(t => t.Name == "decision_decide");
        string schema = decide.JsonSchema.GetRawText();
        Check(schema.Contains("predicate", StringComparison.Ordinal) && schema.Contains("choices", StringComparison.Ordinal) && schema.Contains("levels", StringComparison.Ordinal), "MCP schema must describe all question variants");
        Check(decide.JsonSchema.GetProperty("properties").TryGetProperty("model", out _), "MCP must expose a model override");
        Check(!decide.JsonSchema.TryGetProperty("required", out var required) || !required.EnumerateArray().Any(p => p.GetString() == "model"), "MCP model must be optional in the published schema");
        var catalog = await client.CallToolAsync("decision_models", cancellationToken: timeout.Token);
        Check(catalog.IsError != true && catalog.StructuredContent is not null, "MCP must return structured catalog content");
        if (!live)
        {
            Equal(defaultModel, JsonSerializer.SerializeToElement(catalog.StructuredContent).GetProperty("models")[0].GetProperty("id").GetString());
            var invalid = await client.CallToolAsync("decision_decide", new Dictionary<string, object?>
            {
                ["request"] = new { state = "test", questions = new { q = new { type = "choice", instructions = "Choose", choices = new { only = "one" } } } }
            }, cancellationToken: timeout.Token);
            Check(invalid.IsError == true, "invalid MCP input must become a tool error before authentication");
            return;
        }
        foreach (string model in new[] { defaultModel, "microsoft-decision-1" })
        {
            Dictionary<string, object?> arguments = new()
            {
                ["request"] = JsonSerializer.SerializeToElement(Ticket, JsonData.Options)
            };
            // First call omits model and inherits the CLI default; the second overrides it.
            if (model != defaultModel) arguments["model"] = model;
            var result = await client.CallToolAsync("decision_decide", arguments, cancellationToken: timeout.Token);
            Check(result.IsError != true && result.StructuredContent is not null, $"live MCP result for {model}");
            var normalized = JsonSerializer.SerializeToElement(result.StructuredContent);
            Equal(model, normalized.GetProperty("requestedModel").GetString());
            Equal("sdk", normalized.GetProperty("transport").GetString());
            Equal("billing", normalized.GetProperty("answers").GetProperty("team").GetProperty("choice").GetString());
        }
    }

    private static JsonElement Fixture(string name) => JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "tests", "fixtures", name))).RootElement.Clone();
    private static JsonNode FixtureNode(string name) => JsonNode.Parse(File.ReadAllText(Path.Combine(Root, "tests", "fixtures", name)))!;
    private static HttpResponseMessage JsonResponse(JsonElement element) => new(HttpStatusCode.OK) { Content = new StringContent(element.GetRawText(), Encoding.UTF8, "application/json") };
    private static string SourceDirectory([CallerFilePath] string file = "") => Path.GetDirectoryName(file)!;
    private static void Check(bool condition, string message) { assertions++; if (!condition) throw new Exception(message); }
    private static void Equal<T>(T expected, T actual) { assertions++; if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, received {actual}"); }
    private static void Throws(Action action, string message) { var e = Throws<DecisionException>(action); Check(e.Message.Contains(message, StringComparison.OrdinalIgnoreCase), $"Expected error containing '{message}': {e.Message}"); }
    private static T Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T e) { assertions++; return e; }
        throw new Exception($"Expected {typeof(T).Name}");
    }
    private static async Task<DecisionException> ThrowsAsync(Func<Task> action, string message)
    {
        try { await action(); }
        catch (DecisionException e) { Check(e.Message.Contains(message, StringComparison.OrdinalIgnoreCase), $"Expected error containing '{message}': {e.Message}"); return e; }
        throw new Exception("Expected DecisionException");
    }

    private sealed class TestCredentials : ICredentialSource
    {
        public string Token { get; set; } = "fixture-token";
        public string Source { get; set; } = "test";
        public string Header { get; set; } = "Authorization";
        public ValueTask<Credential> GetAsync(CancellationToken cancellationToken) => ValueTask.FromResult(new Credential(Token, Source, Header));
    }

    private sealed class MockHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Callback { get; set; } = callback;
        public int Gets { get; private set; }
        public int Posts { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get) Gets++;
            if (request.Method == HttpMethod.Post) Posts++;
            return Callback(request, cancellationToken);
        }
    }

    private sealed class EnvironmentScope : IDisposable
    {
        private readonly Dictionary<string, string?> saved;
        public EnvironmentScope(params string[] names)
        {
            saved = names.ToDictionary(n => n, Environment.GetEnvironmentVariable);
            foreach (string name in names) Environment.SetEnvironmentVariable(name, null);
        }
        public void Dispose() { foreach (var (name, value) in saved) Environment.SetEnvironmentVariable(name, value); }
    }
}
