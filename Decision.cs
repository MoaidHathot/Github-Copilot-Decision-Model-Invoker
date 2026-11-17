#!/usr/bin/env -S dotnet --
#:property TargetFramework=net11.0
#:property LangVersion=15.0
#:property Nullable=enable
#:property PublishAot=false
#:property PackAsTool=true
#:property PackageId=Copilot.Decision.CLI
#:property ToolCommandName=gh-decide
#:property Version=0.1.0
#:property Authors=MoaidHathot
#:property Description="Experimental CLI and MCP playground for GitHub Copilot decision models. For demos, testing, and experimentation; not production use."
#:property PackageTags=copilot;decisions;cli;mcp;experimental
#:property PackageLicenseExpression=Unlicense
#:property PackageReadmeFile=README.md
#:property PackageProjectUrl=https://github.com/MoaidHathot/Github-Copilot-Decision-Model-Invoker
#:property RepositoryUrl=https://github.com/MoaidHathot/Github-Copilot-Decision-Model-Invoker
#:property RepositoryType=git
#:property SelfContained=false
#:property NoDefaultExcludes=true
#:property ToolPackageRuntimeIdentifiers="$([MSBuild]::ValueOrDefault('$(GH_DECIDE_PACKAGE_RIDS)', '$(NETCoreSdkPortableRuntimeIdentifier)'))"
#:property RuntimeIdentifiers=$(ToolPackageRuntimeIdentifiers)
#:package ModelContextProtocol@1.4.1
#:package Microsoft.Extensions.Hosting@11.0.0
#:package Azure.Identity@1.21.0
#:package GitHub.Copilot.SDK@1.0.34

using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using CopilotSdk = GitHub.Copilot;
using CopilotRpc = GitHub.Copilot.Rpc;

public static class Program
{
    public static Task<int> Main(string[] args) => DecisionCli.RunAsync(args);
}

public static class AppInfo
{
    public const string CommandName = "gh-decide";
    public static string Version { get; } = typeof(Program).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "unknown";
    public static string UserAgent => $"{CommandName}/{Version}";
}

// The public contract is independent of the hosting provider and its wire protocol.
public sealed record DecisionRequest(
    JsonElement State,
    Dictionary<string, DecisionQuestion> Questions,
    string[]? Images = null)
{
    public void Validate()
    {
        if (State.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ||
            State.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(State.GetString()))
            throw new DecisionException("state must contain text or JSON evidence.");
        if (Questions is null || Questions.Count is < 1 or > 400)
            throw new DecisionException("Provide between 1 and 400 named questions.");
        foreach (var (name, question) in Questions)
        {
            if (string.IsNullOrWhiteSpace(name) || question is null || string.IsNullOrWhiteSpace(question.Instructions))
                throw new DecisionException("Every question needs a nonempty name and instructions.");
            switch (question)
            {
                case ChoiceQuestion c when c.Choices is null || c.Choices.Count is < 2 or > 255 ||
                    c.Choices.Any(p => string.IsNullOrWhiteSpace(p.Key) || string.IsNullOrWhiteSpace(p.Value)):
                    throw new DecisionException($"Question '{name}' needs 2-255 choices, each with a label and description.");
                case ScoreQuestion s when s.Levels is null || s.Levels.Length is < 2 or > 10 ||
                    s.Levels.Any(string.IsNullOrWhiteSpace) || s.Levels.Distinct(StringComparer.Ordinal).Count() != s.Levels.Length:
                    throw new DecisionException($"Question '{name}' needs 2-10 distinct levels, ordered lowest to highest.");
            }
        }
        if (Images is { Length: > 128 })
            throw new DecisionException("At most 128 images can be supplied.");
        foreach (var image in Images ?? [])
        {
            if (string.IsNullOrWhiteSpace(image) ||
                !(image.StartsWith("data:image/", StringComparison.Ordinal) && image.Contains(";base64,", StringComparison.Ordinal)) &&
                !(Uri.TryCreate(image, UriKind.Absolute, out var uri) && uri.Scheme == "https"))
                throw new DecisionException("Images must be HTTPS URLs or base64 image data URLs.");
        }
    }
}

// C# 15 closed hierarchies make protocol-mapping switches exhaustive.
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(PredicateQuestion), "predicate")]
[JsonDerivedType(typeof(ChoiceQuestion), "choice")]
[JsonDerivedType(typeof(ScoreQuestion), "score")]
public closed record DecisionQuestion(string Instructions);
public sealed record PredicateQuestion(string Instructions) : DecisionQuestion(Instructions);
public sealed record ChoiceQuestion(string Instructions, Dictionary<string, string> Choices) : DecisionQuestion(Instructions);
public sealed record ScoreQuestion(string Instructions, string[] Levels) : DecisionQuestion(Instructions);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(PredicateAnswer), "predicate")]
[JsonDerivedType(typeof(ChoiceAnswer), "choice")]
[JsonDerivedType(typeof(ScoreAnswer), "score")]
[JsonDerivedType(typeof(RefusalAnswer), "refusal")]
public closed record DecisionAnswer;
public sealed record PredicateAnswer(double Probability) : DecisionAnswer;
public sealed record ChoiceAnswer(string Choice, Dictionary<string, double> Probabilities, double? Confidence) : DecisionAnswer
{
    public double? NormalizedConfidence { get; init; }
    public bool? Tied { get; init; }
}
public sealed record ScoreAnswer(double Score, double[] Probabilities, string[] Levels, double? Confidence) : DecisionAnswer
{
    public double? NormalizedConfidence { get; init; }
}
public sealed record RefusalAnswer(string? Reason) : DecisionAnswer;

public sealed record DecisionResult(
    string Provider,
    string RequestedModel,
    string Model,
    string Protocol,
    string CredentialSource,
    double ElapsedMilliseconds,
    Dictionary<string, DecisionAnswer> Answers,
    JsonElement? Usage)
{
    public string Transport { get; init; } = "http";
    public string RawFormat { get; init; } = "provider-json";
    [JsonIgnore] public JsonElement Raw { get; init; }
}

public sealed record DecisionModel(
    string Id,
    string Name,
    string[] SupportedEndpoints,
    bool? ModelPickerEnabled = null,
    string? PolicyState = null)
{
    public bool? IsRuntimeDefault { get; init; }
    public DecisionLimits? Limits { get; init; }
}

public sealed record DecisionLimits(long MaxQuestions, long MinChoices, long MaxChoices, long MinLevels,
    long MaxLevels, long MaxRequestBytes, long MaxJsonDepth, long? ContextPlusQuestionTokens);

public sealed record ModelCatalog(string Provider, string Source, string Endpoint, string? CredentialSource, DecisionModel[] Models)
{
    public string Transport { get; init; } = "http";
    public string? DefaultModel { get; init; }
}

public interface IDecisionProvider
{
    Task<ModelCatalog> ListModelsAsync(CancellationToken cancellationToken);
    Task<DecisionResult> DecideAsync(DecisionRequest request, string? model, CancellationToken cancellationToken);
}

public interface IDecisionProtocol
{
    string Name { get; }
    JsonObject CreatePayload(string model, DecisionRequest request);
    Dictionary<string, DecisionAnswer> ReadAnswers(JsonElement response, DecisionRequest request);
}

public sealed class SystemOneProtocol : IDecisionProtocol
{
    public string Name => "systemone";

    public JsonObject CreatePayload(string model, DecisionRequest request)
    {
        if (request.Images is { Length: > 0 })
            throw new DecisionException("Microsoft-Decision-1/System One is text-only. Choose a Decisions model for images.");
        JsonObject questions = [];
        foreach (var (name, question) in request.Questions)
        {
            questions[name] = question switch
            {
                PredicateQuestion q => new JsonObject { ["type"] = "noul", ["instructions"] = q.Instructions },
                ChoiceQuestion q => new JsonObject { ["type"] = "choice", ["instructions"] = q.Instructions, ["criteria"] = JsonData.Node(q.Choices) },
                ScoreQuestion q => new JsonObject { ["type"] = "score", ["instructions"] = q.Instructions, ["criteria"] = JsonData.Node(q.Levels) }
            };
        }
        return new JsonObject { ["model"] = model, ["state"] = JsonData.Node(request.State), ["questions"] = questions };
    }

    public Dictionary<string, DecisionAnswer> ReadAnswers(JsonElement response, DecisionRequest request)
    {
        var answers = Wire.Required(response, "answers", JsonValueKind.Object);
        Wire.CheckNames(answers.EnumerateObject().Select(p => p.Name), request);
        return request.Questions.ToDictionary(
            p => p.Key, p => Wire.ReadAnswer(answers.GetProperty(p.Key), p.Value, systemOne: true), StringComparer.Ordinal);
    }
}

public sealed class DecisionsProtocol : IDecisionProtocol
{
    public string Name => "decisions";

    public JsonObject CreatePayload(string model, DecisionRequest request)
    {
        JsonArray questions = [];
        foreach (var (name, question) in request.Questions)
        {
            JsonObject item = question switch
            {
                PredicateQuestion q => new JsonObject { ["type"] = "predicate", ["instructions"] = q.Instructions },
                ChoiceQuestion q => new JsonObject
                {
                    ["type"] = "choice", ["instructions"] = q.Instructions,
                    ["choices"] = JsonData.Node(q.Choices.Select(p => new { value = p.Key, description = p.Value }).ToArray())
                },
                ScoreQuestion q => new JsonObject
                {
                    ["type"] = "score", ["instructions"] = q.Instructions,
                    ["levels"] = JsonData.Node(q.Levels.Select(label => new { label }).ToArray())
                }
            };
            item["name"] = name;
            questions.Add(item);
        }

        // Text-encode JSON evidence for compatibility with both beta and GA gateways.
        // Never reinterpret JSON keys as message roles, tools, or image URLs.
        string text = request.State.ValueKind == JsonValueKind.String ? request.State.GetString()! : request.State.GetRawText();
        JsonNode input = JsonValue.Create(text)!;
        if (request.Images is { Length: > 0 })
        {
            JsonArray content = [new JsonObject { ["type"] = "input_text", ["text"] = text }];
            foreach (string image in request.Images)
                content.Add(new JsonObject { ["type"] = "input_image", ["image_url"] = image });
            input = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = content });
        }
        return new JsonObject { ["model"] = model, ["input"] = input, ["questions"] = questions };
    }

    public Dictionary<string, DecisionAnswer> ReadAnswers(JsonElement response, DecisionRequest request)
    {
        var answers = Wire.Required(response, "answers", JsonValueKind.Array).EnumerateArray().ToArray();
        var names = answers.Select(a => Wire.Text(a, "name")).ToArray();
        Wire.CheckNames(names, request);
        return answers.ToDictionary(a => Wire.Text(a, "name"),
            a => Wire.ReadAnswer(a, request.Questions[Wire.Text(a, "name")], systemOne: false), StringComparer.Ordinal);
    }
}

public static class Wire
{
    public static JsonElement Required(JsonElement element, string property, JsonValueKind kind)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value) || value.ValueKind != kind)
            throw new DecisionException($"Invalid provider response: '{property}' must be {kind}.");
        return value;
    }

    public static string Text(JsonElement element, string property) => Required(element, property, JsonValueKind.String).GetString()!;

    public static double Number(JsonElement element, string property, double min = 0, double max = 1)
    {
        var value = Required(element, property, JsonValueKind.Number);
        if (!value.TryGetDouble(out double number) || !double.IsFinite(number) || number < min || number > max)
            throw new DecisionException($"Invalid provider response: '{property}' must be between {min} and {max}.");
        return number;
    }

    public static void CheckNames(IEnumerable<string> names, DecisionRequest request)
    {
        string[] actual = names.ToArray();
        // C# 15 collection-constructor arguments preserve ordinal key semantics.
        HashSet<string> expected = [with(StringComparer.Ordinal), .. request.Questions.Keys];
        if (actual.Length != expected.Count || actual.Distinct(StringComparer.Ordinal).Count() != actual.Length || !expected.SetEquals(actual))
            throw new DecisionException("Invalid provider response: answers must match the requested question names exactly.");
    }

    public static DecisionAnswer ReadAnswer(JsonElement answer, DecisionQuestion question, bool systemOne)
    {
        string type = Text(answer, "type");
        if (type == "refusal")
            return new RefusalAnswer(answer.TryGetProperty("reason", out var reason) && reason.ValueKind == JsonValueKind.String ? reason.GetString() : null);
        string expected = question switch { PredicateQuestion => systemOne ? "noul" : "predicate", ChoiceQuestion => "choice", ScoreQuestion => "score" };
        if (type != expected)
            throw new DecisionException($"Invalid provider response: expected '{expected}', received '{type}'.");
        double? confidence = answer.TryGetProperty("confidence", out var c) && c.ValueKind != JsonValueKind.Null ? Number(answer, "confidence") : null;
        return question switch
        {
            PredicateQuestion => new PredicateAnswer(Number(answer, systemOne ? "noul" : "probability")),
            ChoiceQuestion q => ReadChoice(answer, q, confidence, systemOne),
            ScoreQuestion q => ReadScore(answer, q, confidence, systemOne)
        };
    }

    private static ChoiceAnswer ReadChoice(JsonElement answer, ChoiceQuestion question, double? confidence, bool systemOne)
    {
        string choice = Text(answer, "choice");
        if (!question.Choices.ContainsKey(choice))
            throw new DecisionException("Invalid provider response: selected choice was not supplied in the request.");
        var probabilities = ReadProbabilities(answer, systemOne, numericKeys: false);
        if (probabilities.Count != question.Choices.Count || question.Choices.Keys.Any(k => !probabilities.ContainsKey(k)))
            throw new DecisionException("Invalid provider response: choice probabilities do not cover the supplied choices.");
        return new(choice, probabilities, confidence);
    }

    private static ScoreAnswer ReadScore(JsonElement answer, ScoreQuestion question, double? confidence, bool systemOne)
    {
        double score = Number(answer, "score", max: question.Levels.Length - 1);
        var probabilities = ReadProbabilities(answer, systemOne, numericKeys: true);
        string[] keys = Enumerable.Range(0, question.Levels.Length).Select(i => i.ToString(CultureInfo.InvariantCulture)).ToArray();
        if (probabilities.Count != keys.Length || keys.Any(k => !probabilities.ContainsKey(k)))
            throw new DecisionException("Invalid provider response: score probabilities do not cover the supplied levels.");
        return new(score, keys.Select(k => probabilities[k]).ToArray(), question.Levels, confidence);
    }

    private static Dictionary<string, double> ReadProbabilities(JsonElement answer, bool systemOne, bool numericKeys)
    {
        Dictionary<string, double> result = new(StringComparer.Ordinal);
        if (systemOne)
        {
            var probabilities = Required(answer, "probabilities", JsonValueKind.Object);
            foreach (var property in probabilities.EnumerateObject())
            {
                if (!result.TryAdd(property.Name, Number(probabilities, property.Name)))
                    throw new DecisionException("Invalid provider response: duplicate probability label.");
            }
        }
        else
        {
            foreach (var item in Required(answer, "probabilities", JsonValueKind.Array).EnumerateArray())
            {
                string key;
                if (numericKeys)
                {
                    var value = Required(item, "value", JsonValueKind.Number);
                    if (!value.TryGetInt32(out int index))
                        throw new DecisionException("Invalid provider response: score level index must be an integer.");
                    key = index.ToString(CultureInfo.InvariantCulture);
                }
                else key = Text(item, "value");
                if (!result.TryAdd(key, Number(item, "probability")))
                    throw new DecisionException("Invalid provider response: duplicate probability label.");
            }
        }
        return result;
    }
}

public sealed record ProviderOptions
{
    public string Provider { get; init; } = "copilot";
    public string? Model { get; init; }
    public string? Endpoint { get; init; }
    public string Protocol { get; init; } = "auto";
    public string Auth { get; init; } = "auto";
    public string Transport { get; init; } = "auto";
    public string? CopilotCliPath { get; init; }
    public string? Account { get; init; }
    public string? CopilotConfig { get; init; }
    public string? ApiKeyEnv { get; init; }
    public int TimeoutSeconds { get; init; } = 60;

    [JsonIgnore] public string EffectiveTransport => Transport == "auto" ? Provider == "copilot" ? "sdk" : "http" : Transport;

    public string EffectiveModel(string? model)
    {
        string selected = model ?? Model ?? (Provider switch
        {
            "copilot" => "microsoft-decision-1",
            "openai" => "gpt-6-luna",
            _ => throw new DecisionException("Foundry requires --model with your deployment name.")
        });
        return string.IsNullOrWhiteSpace(selected) ? throw new DecisionException("model cannot be empty.") : selected;
    }

    public void Validate()
    {
        if (Provider is not ("copilot" or "foundry" or "openai"))
            throw new DecisionException("provider must be copilot, foundry, or openai.");
        if (Protocol is not ("auto" or "systemone" or "decisions"))
            throw new DecisionException("protocol must be auto, systemone, or decisions.");
        if (Transport is not ("auto" or "sdk" or "http") || Provider != "copilot" && EffectiveTransport != "http")
            throw new DecisionException("transport must be auto, sdk, or http. Only Copilot supports sdk.");
        if (Provider == "copilot" && EffectiveTransport == "sdk")
        {
            if (Endpoint is not null || Protocol != "auto")
                throw new DecisionException("The Copilot SDK owns its endpoints and protocol. Use --transport http with --endpoint or --protocol.");
            if (CopilotConfig is not null && !Path.GetFileName(CopilotConfig).Equals("config.json", StringComparison.OrdinalIgnoreCase))
                throw new DecisionException("The SDK requires a Copilot config named config.json. Use --transport http for other filenames.");
        }
        else if (CopilotCliPath is not null)
            throw new DecisionException("--copilot-cli-path is only used with the Copilot SDK transport.");
        if (TimeoutSeconds is < 1 or > 3600)
            throw new DecisionException("timeoutSeconds must be between 1 and 3600.");
        if (Model is not null && string.IsNullOrWhiteSpace(Model))
            throw new DecisionException("model cannot be empty.");
        if (Provider == "foundry" && (string.IsNullOrWhiteSpace(Endpoint) || Protocol == "auto"))
            throw new DecisionException("Foundry requires --endpoint (resource root), --model (deployment), and --protocol systemone|decisions.");
        if (Provider == "openai" && Protocol == "systemone")
            throw new DecisionException("The OpenAI provider uses the decisions protocol.");
        string[] allowed = Provider switch
        {
            "copilot" => ["auto", "copilot-cli", "gh", "env"],
            "foundry" => ["auto", "azure-cli", "entra", "api-key"],
            _ => ["auto", "api-key"]
        };
        if (!allowed.Contains(Auth, StringComparer.Ordinal))
            throw new DecisionException($"Auth '{Auth}' is not valid for {Provider}. Choose {string.Join(", ", allowed)}.");
        if (Auth == "env" && Account is not null)
            throw new DecisionException("--account cannot select the owner of an environment token. Use --auth copilot-cli or gh.");
    }
}

public sealed class Credential(string value, string source, string header = "Authorization", string scheme = "Bearer")
{
    public string Value { get; } = value;
    public string Source { get; } = source;
    public void Apply(HttpRequestMessage request)
    {
        if (header == "Authorization") request.Headers.Authorization = new AuthenticationHeaderValue(scheme, Value);
        else request.Headers.Add(header, Value);
    }
    public override string ToString() => Source;
}

public interface ICredentialSource
{
    ValueTask<Credential> GetAsync(CancellationToken cancellationToken);
}

public sealed record DecisionTarget(string Model, Uri Endpoint, IDecisionProtocol Protocol, Credential Credential);

public abstract class HttpDecisionProvider(ProviderOptions options, HttpClient http) : IDecisionProvider
{
    protected ProviderOptions Options { get; } = options;
    protected HttpClient Http { get; } = http;
    public abstract Task<ModelCatalog> ListModelsAsync(CancellationToken cancellationToken);
    protected abstract Task<DecisionTarget> ResolveAsync(string model, CancellationToken cancellationToken);

    public async Task<DecisionResult> DecideAsync(DecisionRequest request, string? model, CancellationToken cancellationToken)
    {
        request.Validate();
        var watch = Stopwatch.StartNew();
        var target = await ResolveAsync(Options.EffectiveModel(model), cancellationToken);
        using var message = new HttpRequestMessage(HttpMethod.Post, target.Endpoint)
        {
            Content = new StringContent(target.Protocol.CreatePayload(target.Model, request).ToJsonString(JsonData.Options), Encoding.UTF8, "application/json")
        };
        target.Credential.Apply(message);
        if (Options.Provider == "copilot") HttpTransport.AddCopilotHeaders(message);
        var raw = await HttpTransport.SendAsync(Http, message, cancellationToken);
        var answers = target.Protocol.ReadAnswers(raw, request);
        return new(Options.Provider, target.Model, Wire.Text(raw, "model"), target.Protocol.Name,
            target.Credential.Source, watch.Elapsed.TotalMilliseconds, answers,
            raw.TryGetProperty("usage", out var usage) ? usage.Clone() : null) { Raw = raw };
    }
}

public sealed class CopilotDecisionProvider : HttpDecisionProvider
{
    private readonly ICredentialSource credentials;
    private readonly Uri endpoint;
    private readonly SemaphoreSlim catalogLock = new(1, 1);
    private CachedCatalog? cached;
    private sealed record CachedCatalog(string Token, DateTimeOffset Expires, ModelCatalog Catalog);

    public CopilotDecisionProvider(ProviderOptions options, HttpClient http, ICredentialSource? credentials = null) : base(options, http)
    {
        this.credentials = credentials ?? new CopilotCredentialSource(options);
        endpoint = HttpTransport.ResourceRoot(options.Endpoint ?? "https://api.githubcopilot.com");
        if (endpoint.Scheme != "https" || !endpoint.Host.EndsWith(".githubcopilot.com", StringComparison.OrdinalIgnoreCase))
            throw new DecisionException("Copilot endpoints must be HTTPS hosts under githubcopilot.com.");
    }

    public override async Task<ModelCatalog> ListModelsAsync(CancellationToken cancellationToken) =>
        await CatalogAsync(await credentials.GetAsync(cancellationToken), cancellationToken);

    private async Task<ModelCatalog> CatalogAsync(Credential credential, CancellationToken cancellationToken)
    {
        await catalogLock.WaitAsync(cancellationToken);
        try
        {
            if (cached is { } entry && entry.Token == credential.Value && entry.Expires > DateTimeOffset.UtcNow)
                return entry.Catalog;
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(endpoint, "models"));
            credential.Apply(request);
            HttpTransport.AddCopilotHeaders(request);
            var response = await HttpTransport.SendAsync(Http, request, cancellationToken);
            List<DecisionModel> models = [];
            foreach (var model in Wire.Required(response, "data", JsonValueKind.Array).EnumerateArray())
            {
                string[] paths = model.TryGetProperty("supported_endpoints", out var supported) && supported.ValueKind == JsonValueKind.Array
                    ? supported.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToArray() : [];
                if (!paths.Any(p => p is "/v1/systemone" or "/v1/decisions")) continue;
                string id = Wire.Text(model, "id");
                string name = model.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : id;
                bool? picker = model.TryGetProperty("model_picker_enabled", out var enabled) && enabled.ValueKind is JsonValueKind.True or JsonValueKind.False ? enabled.GetBoolean() : null;
                string? policy = model.TryGetProperty("policy", out var p) && p.ValueKind == JsonValueKind.Object &&
                    p.TryGetProperty("state", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
                models.Add(new(id, name, paths, picker, policy));
            }
            var catalog = new ModelCatalog("copilot", "authenticated /models", endpoint.ToString(), credential.Source, models.ToArray());
            cached = new(credential.Value, DateTimeOffset.UtcNow.AddMinutes(5), catalog);
            return catalog;
        }
        finally { catalogLock.Release(); }
    }

    protected override async Task<DecisionTarget> ResolveAsync(string model, CancellationToken cancellationToken)
    {
        var credential = await credentials.GetAsync(cancellationToken);
        var catalog = await CatalogAsync(credential, cancellationToken);
        if (model.Equals("gpt-6-luna-decision", StringComparison.OrdinalIgnoreCase)) model = "gpt-6-luna-decisions";
        var selected = catalog.Models.FirstOrDefault(m => m.Id.Equals(model, StringComparison.OrdinalIgnoreCase))
            ?? throw new DecisionException($"Decision model '{model}' is not advertised for this Copilot login. Run 'models' to see available IDs.");
        if (selected.PolicyState is "disabled" or "unconfigured")
            throw new DecisionException($"Copilot model '{selected.Id}' has policy '{selected.PolicyState}'. Enable it in your GitHub model settings before calling it.");
        string? path = Options.Protocol == "auto"
            ? selected.SupportedEndpoints.FirstOrDefault(p => p is "/v1/systemone" or "/v1/decisions")
            : selected.SupportedEndpoints.FirstOrDefault(p => p == $"/v1/{Options.Protocol}");
        if (path is null)
            throw new DecisionException($"Model '{selected.Id}' does not advertise protocol '{Options.Protocol}'.");
        IDecisionProtocol protocol = path == "/v1/systemone" ? new SystemOneProtocol() : new DecisionsProtocol();
        return new(selected.Id, new Uri(endpoint, path.TrimStart('/')), protocol, credential);
    }
}

public sealed class FoundryDecisionProvider : HttpDecisionProvider
{
    private readonly ICredentialSource credentials;
    private readonly IDecisionProtocol protocol;
    private readonly Uri endpoint;

    public FoundryDecisionProvider(ProviderOptions options, HttpClient http, ICredentialSource? credentials = null) : base(options, http)
    {
        this.credentials = credentials ?? new FoundryCredentialSource(options);
        protocol = options.Protocol == "systemone" ? new SystemOneProtocol() : new DecisionsProtocol();
        endpoint = new Uri(HttpTransport.ResourceRoot(options.Endpoint!),
            protocol.Name == "systemone" ? "providers/microsoft/v1/systemone" : "openai/v1/decisions");
        _ = options.EffectiveModel(null);
    }

    public override Task<ModelCatalog> ListModelsAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new ModelCatalog("foundry", "configured deployment (not a deployment discovery API)", endpoint.ToString(), null,
            [new(Options.EffectiveModel(null), Options.EffectiveModel(null), [endpoint.AbsolutePath])]));

    protected override async Task<DecisionTarget> ResolveAsync(string model, CancellationToken cancellationToken) =>
        new(model, endpoint, protocol, await credentials.GetAsync(cancellationToken));
}

public sealed class OpenAiDecisionProvider(ProviderOptions options, HttpClient http, ICredentialSource? credentials = null)
    : HttpDecisionProvider(options, http)
{
    private readonly ICredentialSource credentials = credentials ?? new ApiKeyCredentialSource(options.ApiKeyEnv ?? "OPENAI_API_KEY", azure: false);
    private readonly Uri endpoint = new(HttpTransport.ResourceRoot(options.Endpoint ?? "https://api.openai.com"), "v1/decisions");

    public override Task<ModelCatalog> ListModelsAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new ModelCatalog("openai", "configured model", endpoint.ToString(), null,
            [new(Options.EffectiveModel(null), Options.EffectiveModel(null), [endpoint.AbsolutePath])]));

    protected override async Task<DecisionTarget> ResolveAsync(string model, CancellationToken cancellationToken) =>
        new(model, endpoint, new DecisionsProtocol(), await credentials.GetAsync(cancellationToken));
}

#pragma warning disable GHCP001 // Opt in to the pinned SDK's experimental sessionless Decisions RPC.

public sealed record SdkDecisionCall(CopilotRpc.DecisionContext Context, IList<CopilotRpc.DecisionQuestion> Questions,
    string Model, int TimeoutMs, string? SelectionId);

// Small runtime boundary for offline provider/lifecycle tests. The production implementation owns one SDK client.
public interface ICopilotDecisionRuntime : IAsyncDisposable
{
    string CredentialSource { get; }
    string? SelectionId { get; }
    Task StartAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<CopilotRpc.DecisionModelInfo>> ListModelsAsync(CancellationToken cancellationToken);
    Task<CopilotRpc.DecisionsAskResult> AskAsync(SdkDecisionCall call, CancellationToken cancellationToken);
}

public sealed class CopilotDecisionRuntime(ProviderOptions options) : ICopilotDecisionRuntime
{
    private CopilotSdk.CopilotClient? client;
    public string CredentialSource { get; private set; } = "copilot-sdk";
    public string? SelectionId => null;

    internal static Dictionary<string, string> ChildEnvironment(ProviderOptions options)
    {
        var environment = Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string)e.Value!, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        // The flag belongs to this runtime process, not the machine or the MCP client's environment.
        environment["COPILOT_CLI_ENABLED_FEATURE_FLAGS"] = "DECISIONS_API";
        if (options.Auth != "auto" || options.Account is not null)
            foreach (string name in new[] { "COPILOT_GITHUB_TOKEN", "GH_TOKEN", "GITHUB_TOKEN" }) environment.Remove(name);
        return environment;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // 1.0.34's account.getAllUsers binding requires a gh-cli token that the runtime redacts.
        // Resolve an explicitly named account through the existing login helper instead of that broken roster API.
        Credential? explicitCredential = options.Auth is "gh" or "env" || options.Account is not null
            ? await new CopilotCredentialSource(options).GetAsync(cancellationToken) : null;
        string? executable = options.CopilotCliPath;
        if (executable is not null)
        {
            executable = Path.GetFullPath(executable);
            if (!File.Exists(executable)) throw new DecisionException("The --copilot-cli-path executable does not exist.");
        }
        var candidate = new CopilotSdk.CopilotClient(new CopilotSdk.CopilotClientOptions
        {
            Connection = CopilotSdk.RuntimeConnection.ForStdio(path: executable, args: executable is null ? null : ["--no-auto-update"]),
            Environment = ChildEnvironment(options),
            UseLoggedInUser = explicitCredential is null,
            GitHubToken = explicitCredential?.Value,
            BaseDirectory = options.CopilotConfig is null ? null : Path.GetDirectoryName(Path.GetFullPath(options.CopilotConfig)),
            LogLevel = CopilotSdk.CopilotLogLevel.None
        });
        try
        {
            await candidate.StartAsync(cancellationToken);
            var status = await candidate.GetStatusAsync(cancellationToken);
            if (status.ProtocolVersion != 3)
                throw new DecisionException("GitHub.Copilot.SDK 1.0.34 requires protocol 3. Use its bundled runtime or CLI 1.0.112.");
            var auth = await candidate.GetAuthStatusAsync(cancellationToken);
            if (!auth.IsAuthenticated || explicitCredential is null && options.Auth == "copilot-cli" && auth.AuthType != "user")
                throw new DecisionException("No matching Copilot CLI login. Run 'copilot login', or select --auth gh.");
            CredentialSource = explicitCredential?.Source ?? $"copilot-sdk:{auth.AuthType}:{auth.Login ?? "current"}";
            client = candidate;
        }
        catch
        {
            await candidate.DisposeAsync();
            throw;
        }
    }

    public async Task<IReadOnlyList<CopilotRpc.DecisionModelInfo>> ListModelsAsync(CancellationToken cancellationToken)
    {
        var result = await client!.Rpc.Models.ListAsync(selectionId: SelectionId, cancellationToken: cancellationToken);
        return result.DecisionModels?.ToArray()
            ?? throw new DecisionException("The runtime did not expose decisionModels. Use the matching SDK runtime with DECISIONS_API enabled.");
    }

    public Task<CopilotRpc.DecisionsAskResult> AskAsync(SdkDecisionCall call, CancellationToken cancellationToken)
    {
        var current = client ?? throw new InvalidOperationException("The SDK runtime is not running.");
        return current.Rpc.Decisions.AskAsync(call.Context, call.Questions, selectionId: call.SelectionId,
            model: call.Model, timeoutMs: call.TimeoutMs, cancellationToken: cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (client is { } owned)
        {
            client = null;
            await owned.DisposeAsync();
        }
    }
}

public sealed class CopilotSdkDecisionProvider : IDecisionProvider, IAsyncDisposable
{
    private readonly ProviderOptions options;
    private readonly ICopilotDecisionRuntime runtime;
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool started;
    private bool disposed;
    private CopilotRpc.DecisionModelInfo[]? models;
    private DateTimeOffset catalogExpires;

    public CopilotSdkDecisionProvider(ProviderOptions options, ICopilotDecisionRuntime? runtime = null)
    {
        this.options = options;
        this.runtime = runtime ?? new CopilotDecisionRuntime(options);
    }

    private async Task<CopilotRpc.DecisionModelInfo[]> ModelsAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!started) { await runtime.StartAsync(cancellationToken); started = true; }
            if (models is null || catalogExpires <= DateTimeOffset.UtcNow)
            {
                models = (await runtime.ListModelsAsync(cancellationToken)).ToArray();
                catalogExpires = DateTimeOffset.UtcNow.AddMinutes(5);
            }
            return models;
        }
        finally { gate.Release(); }
    }

    public Task<ModelCatalog> ListModelsAsync(CancellationToken cancellationToken) => GuardAsync(async () =>
    {
        var available = await ModelsAsync(cancellationToken);
        return new ModelCatalog("copilot", "Copilot SDK models.list / decisionModels", "copilot-sdk://decisions.ask",
            runtime.CredentialSource, available.Select(m => new DecisionModel(m.Id, m.Name, [])
            {
                IsRuntimeDefault = m.IsDefault,
                Limits = Limits(m.Limits)
            }).ToArray()) { Transport = "sdk", DefaultModel = CanonicalModel(options.EffectiveModel(null)) };
    });

    public Task<DecisionResult> DecideAsync(DecisionRequest request, string? model, CancellationToken cancellationToken) => GuardAsync(async () =>
    {
        request.Validate();
        if (request.Images is { Length: > 0 })
            throw new DecisionException("The Copilot SDK supports text and JSON context only. Use --transport http for image requests.");
        var watch = Stopwatch.StartNew();
        string requested = CanonicalModel(options.EffectiveModel(model));
        var available = await ModelsAsync(cancellationToken);
        var selected = available.FirstOrDefault(m => m.Id.Equals(requested, StringComparison.OrdinalIgnoreCase))
            ?? throw new DecisionException($"Decision model '{requested}' is not advertised by this Copilot SDK login. Run 'models'.");
        // --timeout is the whole operation's budget; the SDK RPC also has its own documented 30s maximum.
        var call = SdkMapping.CreateCall(request, selected.Id, Math.Min(options.TimeoutSeconds, 30) * 1000, runtime.SelectionId);
        SdkMapping.ValidateCall(call, request, Limits(selected.Limits));
        var response = await runtime.AskAsync(call, cancellationToken);
        if (response.Model != selected.Id)
            throw new DecisionException("Invalid provider response: the SDK returned a different requested model.");
        var answers = SdkMapping.ReadAnswers(response, request);
        return new DecisionResult("copilot", selected.Id, response.UpstreamModel ?? response.Model, "decisions.ask",
            runtime.CredentialSource, watch.Elapsed.TotalMilliseconds, answers, SdkMapping.Usage(response.Usage))
        {
            Transport = "sdk", RawFormat = "copilot-sdk-json",
            Raw = JsonSerializer.SerializeToElement(response, JsonData.Options)
        };
    });

    internal static string CanonicalModel(string model) => model.Equals("gpt-6-luna-decision", StringComparison.OrdinalIgnoreCase) ? "gpt-6-luna-decisions" : model;

    private static DecisionLimits Limits(CopilotRpc.DecisionModelLimits limits) => new(limits.MaxQuestions,
        limits.MinOptions, limits.MaxOptions, limits.MinLevels, limits.MaxLevels, limits.MaxRequestBytes,
        limits.MaxJsonDepth, limits.ContextPlusQuestionTokens);

    private static async Task<T> GuardAsync<T>(Func<Task<T>> action)
    {
        try { return await action(); }
        catch (Exception e) when (e is IOException or CopilotSdk.RemoteRpcException or Win32Exception or InvalidOperationException or JsonException)
        { throw SdkMapping.Error(e); }
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (disposed) return;
            disposed = true;
            await runtime.DisposeAsync();
        }
        finally { gate.Release(); } // Queued requests can still unwind and observe the disposed state.
    }
}

public static class SdkMapping
{
    public static SdkDecisionCall CreateCall(DecisionRequest request, string model, int timeoutMs, string? selectionId)
    {
        CopilotRpc.DecisionContext context = request.State.ValueKind == JsonValueKind.Object ||
            request.State.ValueKind == JsonValueKind.Array && request.State.EnumerateArray().All(v => v.ValueKind == JsonValueKind.Object)
            ? new CopilotRpc.DecisionContextJson { Value = request.State }
            : new CopilotRpc.DecisionContextText { Text = request.State.ValueKind == JsonValueKind.String ? request.State.GetString()! : request.State.GetRawText() };
        var questions = request.Questions.Select(p => p.Value switch
        {
            PredicateQuestion q => (CopilotRpc.DecisionQuestion)new CopilotRpc.DecisionQuestionBoolean { Name = p.Key, Instructions = q.Instructions },
            ChoiceQuestion q => new CopilotRpc.DecisionQuestionChoice
            {
                Name = p.Key, Instructions = q.Instructions,
                Options = q.Choices.Select(c => new CopilotRpc.DecisionOption { Value = c.Key, Description = c.Value }).ToArray()
            },
            ScoreQuestion q => new CopilotRpc.DecisionQuestionScore
            {
                Name = p.Key, Instructions = q.Instructions,
                Levels = q.Levels.Select(l => new CopilotRpc.DecisionLevel { Label = l, Description = l }).ToArray()
            }
        }).ToArray();
        return new(context, questions, model, timeoutMs, selectionId);
    }

    public static void ValidateCall(SdkDecisionCall call, DecisionRequest request, DecisionLimits limits)
    {
        if (limits.MaxQuestions < 1 || limits.MinChoices < 2 || limits.MaxChoices < limits.MinChoices ||
            limits.MinLevels < 2 || limits.MaxLevels < limits.MinLevels || limits.MaxRequestBytes < 1 || limits.MaxJsonDepth < 1)
            throw new DecisionException("The SDK catalog returned invalid request limits.");
        if (request.Questions.Count > limits.MaxQuestions)
            throw new DecisionException($"This SDK model allows at most {limits.MaxQuestions} questions. Use --transport http for a different request contract.");
        foreach (var (name, question) in request.Questions)
        {
            if (question is ChoiceQuestion choice)
            {
                if (choice.Choices.Count < limits.MinChoices || choice.Choices.Count > limits.MaxChoices)
                    throw new DecisionException($"SDK question '{name}' requires {limits.MinChoices}-{limits.MaxChoices} choices.");
                if (choice.Choices.Keys.Any(k => k.Length is < 1 or > 64 || k.Any(c => !char.IsAsciiLetterOrDigit(c) && !"._:/@-".Contains(c))))
                    throw new DecisionException($"SDK choice values in '{name}' must be 1-64 characters using ASCII letters, digits, or ._:/@-.");
            }
            if (question is ScoreQuestion score && (score.Levels.Length < limits.MinLevels || score.Levels.Length > limits.MaxLevels))
                throw new DecisionException($"SDK question '{name}' requires {limits.MinLevels}-{limits.MaxLevels} levels.");
        }
        if (call.Context is CopilotRpc.DecisionContextJson json && JsonDepth(json.Value) > limits.MaxJsonDepth)
            throw new DecisionException($"SDK JSON context exceeds the {limits.MaxJsonDepth}-level nesting limit.");
        if (JsonSerializer.SerializeToUtf8Bytes(call, JsonData.Options).LongLength > limits.MaxRequestBytes)
            throw new DecisionException($"SDK request exceeds {limits.MaxRequestBytes} serialized bytes. Use --transport http for a different request contract.");
    }

    private static int JsonDepth(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => 1 + value.EnumerateObject().Select(p => JsonDepth(p.Value)).DefaultIfEmpty().Max(),
        JsonValueKind.Array => 1 + value.EnumerateArray().Select(JsonDepth).DefaultIfEmpty().Max(),
        _ => 0
    };

    public static Dictionary<string, DecisionAnswer> ReadAnswers(CopilotRpc.DecisionsAskResult response, DecisionRequest request)
    {
        static string Name(CopilotRpc.DecisionAnswer answer) => answer switch
        {
            CopilotRpc.DecisionAnswerBoolean b => b.Name,
            CopilotRpc.DecisionAnswerChoice c => c.Name,
            CopilotRpc.DecisionAnswerScore s => s.Name,
            CopilotRpc.DecisionAnswerRefused r => r.Name,
            _ => throw new DecisionException("Invalid provider response: unknown SDK answer kind.")
        };
        Wire.CheckNames(response.Answers.Select(Name), request);
        Dictionary<string, DecisionAnswer> result = new(StringComparer.Ordinal);
        foreach (var answer in response.Answers)
        {
            string name = Name(answer);
            JsonObject normalized;
            if (answer is CopilotRpc.DecisionAnswerRefused) { result[name] = new RefusalAnswer(null); continue; }
            if (answer is CopilotRpc.DecisionAnswerBoolean b)
                normalized = new() { ["type"] = "predicate", ["probability"] = b.Probability };
            else if (answer is CopilotRpc.DecisionAnswerChoice c)
                normalized = new()
                {
                    ["type"] = "choice", ["choice"] = c.Value, ["confidence"] = c.VendorConfidence,
                    ["probabilities"] = JsonData.Node(c.Probabilities.Select(p => new { value = p.Value, probability = p.Probability }).ToArray())
                };
            else if (answer is CopilotRpc.DecisionAnswerScore s)
            {
                if (request.Questions[name] is not ScoreQuestion q || !s.Probabilities.Select(p => p.Label).SequenceEqual(q.Levels))
                    throw new DecisionException("Invalid provider response: SDK score levels do not match the request order.");
                normalized = new()
                {
                    ["type"] = "score", ["score"] = s.Score, ["confidence"] = s.VendorConfidence,
                    ["probabilities"] = JsonData.Node(s.Probabilities.Select((p, index) => new { value = index, probability = p.Probability }).ToArray())
                };
            }
            else throw new DecisionException("Invalid provider response: unknown SDK answer kind.");
            var parsed = Wire.ReadAnswer(JsonSerializer.SerializeToElement(normalized), request.Questions[name], systemOne: false);
            // Preserve vendor confidence in the existing field; expose SDK normalization separately.
            result[name] = (parsed, answer) switch
            {
                (ChoiceAnswer choice, CopilotRpc.DecisionAnswerChoice sdk) => choice with { NormalizedConfidence = Probability(sdk.Confidence), Tied = sdk.Tied },
                (ScoreAnswer score, CopilotRpc.DecisionAnswerScore sdk) => score with { NormalizedConfidence = Probability(sdk.Confidence) },
                _ => parsed
            };
        }
        return result;
    }

    private static double Probability(double value) => double.IsFinite(value) && value is >= 0 and <= 1
        ? value : throw new DecisionException("Invalid provider response: SDK confidence must be a probability-range value.");

    public static JsonElement? Usage(CopilotRpc.DecisionUsage? usage)
    {
        if (usage is null) return null;
        JsonObject result = new() { ["input_tokens"] = usage.InputTokens, ["output_tokens"] = usage.OutputTokens };
        JsonObject details = [];
        if (usage.CachedInputTokens is { } cached) details["cached_tokens"] = cached;
        if (usage.CacheWriteTokens is { } written) details["cache_write_tokens"] = written;
        if (details.Count > 0) result["input_tokens_details"] = details;
        if (usage.ReasoningTokens is { } reasoning) result["output_tokens_details"] = new JsonObject { ["reasoning_tokens"] = reasoning };
        return JsonSerializer.SerializeToElement(result);
    }

    public static DecisionException Error(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is not CopilotSdk.RemoteRpcException remote) continue;
            if (remote.ErrorData is { ValueKind: JsonValueKind.Object } data && data.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.String)
            {
                string? code = kind.GetString();
                string message = code switch
                {
                    "notEnabled" => "The runtime's DECISIONS_API feature is not enabled.",
                    "noModel" => "The selected model is unavailable for this account; run 'models'.",
                    "unauthenticated" => "Authentication failed; run 'copilot login' or check the selected credential.",
                    "invalidRequest" or "tooLarge" => "The request exceeds a runtime rule, size, or token limit; check 'models' or select --transport http.",
                    "rateLimited" => "Rate limited.",
                    "timeout" => "The SDK decision deadline expired (at most 30 seconds per RPC).",
                    "unavailable" or "overloaded" => "The decision service is currently unavailable.",
                    "upstreamProtocol" => "The runtime could not interpret the upstream decision response.",
                    _ => "The runtime rejected the decision request."
                };
                if (data.TryGetProperty("retryAfterMs", out var retry) && retry.ValueKind == JsonValueKind.Number && retry.TryGetInt64(out long ms) && ms >= 0)
                    message += $" Retry-After: {ms} ms.";
                return new DecisionException($"Copilot SDK: {message}");
            }
            return new DecisionException($"Copilot SDK RPC failed (code {remote.ErrorCode}). Use the matching runtime and authenticate with Copilot.");
        }
        // Never echo arbitrary SDK/backend exception bodies: they can include authentication details.
        return new DecisionException("Copilot SDK runtime connection failed. Use the bundled runtime or --copilot-cli-path pointing to compatible CLI 1.0.112, or select --transport http.");
    }
}

#pragma warning restore GHCP001

public static class HttpTransport
{
    public static HttpClient CreateClient() => new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };

    public static Uri ResourceRoot(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath.Trim('/') != "")
            throw new DecisionException("endpoint must be an HTTPS resource root, without a path, query, credentials, or fragment.");
        return new Uri(uri.GetLeftPart(UriPartial.Authority) + "/");
    }

    public static void AddCopilotHeaders(HttpRequestMessage request)
    {
        request.Headers.Add("Editor-Version", AppInfo.UserAgent);
        request.Headers.Add("Copilot-Integration-Id", "copilot-developer-cli");
        request.Headers.Add("X-Initiator", "user");
        request.Headers.Add("X-Request-Id", Guid.NewGuid().ToString());
    }

    public static async Task<JsonElement> SendAsync(HttpClient client, HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd(AppInfo.UserAgent);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            string detail = body;
            foreach (string secret in new[] { request.Headers.Authorization?.Parameter }
                .Concat(request.Headers.TryGetValues("api-key", out var keys) ? keys : []).OfType<string>())
                detail = detail.Replace(secret, "[redacted]", StringComparison.Ordinal);
            detail = detail.Replace('\r', ' ').Replace('\n', ' ');
            if (detail.Length > 1200) detail = detail[..1200] + "...";
            string hint = response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => " Reauthenticate using the selected provider's login.",
                HttpStatusCode.Forbidden => " Check this account's model entitlement and organization policy.",
                HttpStatusCode.NotFound => " Check the advertised route, deployment name, and regional availability.",
                HttpStatusCode.TooManyRequests => $" Retry-After: {response.Headers.RetryAfter?.ToString() ?? "not supplied"}.",
                _ => ""
            };
            throw new DecisionException($"HTTP {(int)response.StatusCode} from {request.RequestUri?.AbsolutePath}.{hint} {detail}".Trim());
        }
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new DecisionException("Provider returned a non-object JSON response.");
            return document.RootElement.Clone();
        }
        catch (JsonException) { throw new DecisionException("Provider returned invalid JSON."); }
    }
}

public sealed class CopilotCredentialSource(ProviderOptions options) : ICredentialSource
{
    public async ValueTask<Credential> GetAsync(CancellationToken cancellationToken)
    {
        if (options.Auth is "auto" or "env" && options.Account is null)
        {
            foreach (string name in new[] { "COPILOT_GITHUB_TOKEN", "GH_TOKEN", "GITHUB_TOKEN" })
            {
                if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value)
                    return Validate(value.Trim(), $"environment:{name}");
            }
            if (options.Auth == "env") throw new DecisionException("Set COPILOT_GITHUB_TOKEN to an OAuth or Copilot-enabled token.");
        }
        if (options.Auth is "auto" or "copilot-cli")
        {
            var saved = await CopilotLoginStore.ReadAsync(options, cancellationToken);
            if (saved is not null) return Validate(saved.Value, saved.Source);
            if (options.Auth == "copilot-cli")
                throw new DecisionException("No readable Copilot CLI login for this account. Run 'copilot login', or use --auth gh.");
        }
        if (options.Auth is "auto" or "gh")
        {
            List<string> args = ["auth", "token", "--hostname", "github.com"];
            if (options.Account is { } account) args.AddRange(["--user", account]);
            var token = await ChildProcess.CaptureAsync("gh", args, cancellationToken, clearGitHubEnvironment: true);
            if (token is not null) return Validate(token.Trim(), options.Account is null ? "gh:github.com" : $"gh:{options.Account}");
        }
        throw new DecisionException("No GitHub login found. Run 'copilot login' or 'gh auth login --web --hostname github.com', then retry.");
    }

    private static Credential Validate(string token, string source)
    {
        if (token.StartsWith("ghp_", StringComparison.Ordinal))
            throw new DecisionException($"{source} contains a classic PAT. Use a browser OAuth login with copilot or gh.");
        if (token.Length == 0 || token.Any(char.IsWhiteSpace))
            throw new DecisionException($"{source} did not contain a valid single token.");
        return new(token, source);
    }
}

public static class CopilotLoginStore
{
    public static async Task<Credential?> ReadAsync(ProviderOptions options, CancellationToken cancellationToken)
    {
        string path = options.CopilotConfig ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".copilot", "config.json");
        if (!File.Exists(path)) return null;
        JsonNode? config;
        try { config = JsonNode.Parse(await File.ReadAllTextAsync(path, cancellationToken), documentOptions: new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }); }
        catch (JsonException) { throw new DecisionException("Copilot CLI config is not valid JSON."); }
        var users = config?["loggedInUsers"] ?? config?["logged_in_users"];
        var last = config?["lastLoggedInUser"] ?? config?["last_logged_in_user"];
        JsonNode? selected = options.Account is null ? last : (users as JsonArray)?.FirstOrDefault(u =>
            string.Equals(u?["login"]?.GetValue<string>(), options.Account, StringComparison.OrdinalIgnoreCase));
        string? login = selected?["login"]?.GetValue<string>();
        string host = selected?["host"]?.GetValue<string>()?.TrimEnd('/') ?? "https://github.com";
        if (string.IsNullOrWhiteSpace(login)) return null;
        if (host is not ("https://github.com" or "github.com"))
            throw new DecisionException("This utility currently reuses github.com logins. Select a github.com account with --account or --auth gh.");
        host = "https://github.com";
        string account = $"{host}:{login}";
        string? value = null;
        if (OperatingSystem.IsWindows())
        {
            // CLI upgrades can leave revoked credentials behind in an older layout.
            // Prefer the most recently written entry for this exact account.
            value = WindowsCredentialStore.ReadNewest(
                [$"{account}:github.copilot-cli", $"{account}.copilot-cli", $"copilot-cli/{account}"]);
        }
        else if (OperatingSystem.IsMacOS())
        {
            foreach (string key in new[] { $"{account}:github", account })
            {
                value = await ChildProcess.CaptureAsync("security", ["find-generic-password", "-s", "copilot-cli", "-a", key, "-w"], cancellationToken);
                if (value is not null) break;
            }
        }
        else if (OperatingSystem.IsLinux())
        {
            lookup: foreach (string key in new[] { $"{account}:github", account })
            {
                foreach (string attribute in new[] { "username", "account" })
                {
                    value = await ChildProcess.CaptureAsync("secret-tool", ["lookup", "service", "copilot-cli", attribute, key], cancellationToken);
                    if (value is not null) break lookup;
                }
            }
        }
        // The CLI's opt-in plaintext fallback (older CLI versions).
        value ??= selected?["oauthToken"]?.GetValue<string>() ?? selected?["oauth_token"]?.GetValue<string>();
        if (value is null && users is JsonArray array)
        {
            var user = array.FirstOrDefault(u => u?["login"]?.GetValue<string>() == login &&
                (u?["host"]?.GetValue<string>()?.TrimEnd('/') ?? host) == host);
            value = user?["oauthToken"]?.GetValue<string>() ?? user?["oauth_token"]?.GetValue<string>();
        }
        return string.IsNullOrWhiteSpace(value) ? null : new Credential(value.Trim(), $"copilot-cli:{login}");
    }
}

internal static class WindowsCredentialStore
{
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr credential);

    internal static string? ReadNewest(IEnumerable<string> targets)
    {
        string? value = null;
        long newest = long.MinValue;
        foreach (string target in targets)
        {
            string? candidate = Read(target, out long written);
            if (candidate is not null && written > newest) { value = candidate; newest = written; }
        }
        return value;
    }

    private static string? Read(string target, out long written)
    {
        written = 0;
        if (!CredRead(target, 1, 0, out var pointer)) return null;
        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(pointer);
            written = credential.LastWritten;
            if (credential.CredentialBlobSize == 0) return null;
            byte[] bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            try
            {
                // keytar uses UTF-16; current keyring versions can store UTF-8 bytes.
                var encoding = bytes.Length > 1 && bytes[1] == 0 ? Encoding.Unicode : Encoding.UTF8;
                return encoding.GetString(bytes).TrimEnd('\0');
            }
            finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
        }
        finally { CredFree(pointer); }
    }
}

public sealed class ApiKeyCredentialSource(string environment, bool azure) : ICredentialSource
{
    public ValueTask<Credential> GetAsync(CancellationToken cancellationToken)
    {
        string? key = Environment.GetEnvironmentVariable(environment);
        if (string.IsNullOrWhiteSpace(key)) throw new DecisionException($"Set the {environment} environment variable.");
        return ValueTask.FromResult(new Credential(key.Trim(), $"environment:{environment}", azure ? "api-key" : "Authorization"));
    }
}

public sealed class FoundryCredentialSource : ICredentialSource
{
    private readonly ProviderOptions options;
    private readonly TokenCredential credential;

    public FoundryCredentialSource(ProviderOptions options)
    {
        this.options = options;
        credential = options.Auth == "azure-cli" ? new AzureCliCredential() : new DefaultAzureCredential();
    }

    public async ValueTask<Credential> GetAsync(CancellationToken cancellationToken)
    {
        string[] names = options.ApiKeyEnv is { } name ? [name] : ["AZURE_API_KEY", "AZURE_OPENAI_API_KEY"];
        if (options.Auth is "auto" or "api-key")
        {
            foreach (string variable in names)
            {
                if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variable)))
                    return await new ApiKeyCredentialSource(variable, azure: true).GetAsync(cancellationToken);
            }
            if (options.Auth == "api-key") throw new DecisionException($"Set {string.Join(" or ", names)}.");
        }
        try
        {
            var token = await credential.GetTokenAsync(new TokenRequestContext(["https://cognitiveservices.azure.com/.default"]), cancellationToken);
            return new(token.Token, options.Auth == "azure-cli" ? "AzureCliCredential" : "DefaultAzureCredential");
        }
        catch (AuthenticationFailedException)
        {
            throw new DecisionException("Azure authentication failed. Run 'az login' and use --auth azure-cli, or configure DefaultAzureCredential / an API key.");
        }
    }
}

public static class ChildProcess
{
    public static async Task<string?> CaptureAsync(string executable, IEnumerable<string> arguments, CancellationToken cancellationToken, bool clearGitHubEnvironment = false)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        if (clearGitHubEnvironment)
            foreach (string variable in new[] { "GH_TOKEN", "GITHUB_TOKEN", "GH_ENTERPRISE_TOKEN", "GITHUB_ENTERPRISE_TOKEN" }) start.Environment.Remove(variable);
        using var process = new Process { StartInfo = start };
        try { if (!process.Start()) return null; }
        catch (Win32Exception) { return null; }
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
            string output = await stdout;
            _ = await stderr; // Credential helper diagnostics never reach stdout or logs.
            return process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output) ? output : null;
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }
    }
}

public sealed class DecisionService : IAsyncDisposable
{
    private readonly ProviderOptions options;
    private readonly IDecisionProvider provider;

    public DecisionService(ProviderOptions options, HttpClient http, IDecisionProvider? provider = null)
    {
        options.Validate();
        this.options = options;
        this.provider = provider ?? (options.Provider switch
        {
            "copilot" when options.EffectiveTransport == "sdk" => new CopilotSdkDecisionProvider(options),
            "copilot" => new CopilotDecisionProvider(options, http),
            "foundry" => new FoundryDecisionProvider(options, http),
            "openai" => new OpenAiDecisionProvider(options, http),
            _ => throw new DecisionException("Unknown provider.")
        });
    }

    public Task<DecisionResult> DecideAsync(DecisionRequest request, string? model, CancellationToken cancellationToken) =>
        WithTimeoutAsync(ct => provider.DecideAsync(request, model, ct), cancellationToken);

    public Task<ModelCatalog> ModelsAsync(CancellationToken cancellationToken) => WithTimeoutAsync(provider.ListModelsAsync, cancellationToken);

    public ValueTask DisposeAsync() => provider is IAsyncDisposable disposable ? disposable.DisposeAsync() : ValueTask.CompletedTask;

    private async Task<T> WithTimeoutAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
        try { return await action(timeout.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new DecisionException($"Request timed out after {options.TimeoutSeconds} seconds."); }
        catch (HttpRequestException e) { throw new DecisionException($"Network request failed: {e.Message}"); }
    }
}

[McpServerToolType]
public sealed class DecisionTools(DecisionService service)
{
    [McpServerTool(Name = "decision_decide", ReadOnly = true, Destructive = false, Idempotent = false, UseStructuredContent = true)]
    [Description("Evaluate text/JSON evidence against named predicate, choice, or score questions. Returns probabilities, choices, scores, refusals, and usage. Uses the provider configured when this MCP server starts.")]
    public async Task<DecisionResult> Decide(
        [Description("Shared state and named predicate/choice/score questions. Query decision_models for SDK limits. Images require the HTTP transport and decisions protocol.")] DecisionRequest request,
        [Description("Optional model ID (Copilot/OpenAI) or deployment name (Foundry). Omit to use the server's --default-model (or --model) setting. Copilot defaults to microsoft-decision-1 when none is configured.")] string? model = null,
        CancellationToken cancellationToken = default)
    {
        try { return await service.DecideAsync(request, model, cancellationToken); }
        catch (DecisionException e) { throw new McpException(e.Message); }
    }

    [McpServerTool(Name = "decision_models", ReadOnly = true, Destructive = false, Idempotent = true, UseStructuredContent = true)]
    [Description("List Copilot decision models, the configured default, and effective SDK limits. HTTP discovery includes model policies. For Foundry/OpenAI, describe the configured deployment/model instead.")]
    public async Task<ModelCatalog> Models(CancellationToken cancellationToken = default)
    {
        try { return await service.ModelsAsync(cancellationToken); }
        catch (DecisionException e) { throw new McpException(e.Message); }
    }
}

public static class JsonData
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        AllowOutOfOrderMetadataProperties = true,
        AllowDuplicateProperties = false,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };
    public static JsonSerializerOptions Pretty { get; } = new(Options) { WriteIndented = true };
    public static JsonNode? Node<T>(T value) => JsonSerializer.SerializeToNode(value, Options);
    public static DecisionRequest ReadRequest(string json) => JsonSerializer.Deserialize<DecisionRequest>(json, Options)
        ?? throw new DecisionException("Request must be a JSON object.");
}

public sealed class DecisionException(string message) : Exception(message);

public static class DecisionCli
{
    public static async Task<int> RunAsync(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += handler;
        try
        {
            if (args is ["--version"] or ["-v"]) args = ["version"];
            var cli = CliArguments.Parse(args);
            if (cli.Command == "help" || cli.Has("help")) { Console.WriteLine(Help); return 0; }
            if (cli.Command == "version") { Console.WriteLine($"{AppInfo.CommandName} {AppInfo.Version} (.NET 11 / C# 15)"); return 0; }
            var options = Configuration.Load(cli);
            if (cli.Command == "login") return await LoginAsync(options, cancellation.Token);
            using var http = HttpTransport.CreateClient();
            await using var service = new DecisionService(options, http);
            switch (cli.Command)
            {
                case "models":
                    Console.WriteLine(JsonSerializer.Serialize(await service.ModelsAsync(cancellation.Token), JsonData.Pretty));
                    return 0;
                case "decide":
                    var input = await ReadInputAsync(cli, cancellation.Token);
                    var result = await service.DecideAsync(input, null, cancellation.Token);
                    Console.WriteLine(cli.Has("raw") ? JsonSerializer.Serialize(result.Raw, JsonData.Pretty) : JsonSerializer.Serialize(result, JsonData.Pretty));
                    return 0;
                case "mcp":
                    var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
                    builder.Logging.ClearProviders();
                    builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
                    builder.Logging.SetMinimumLevel(LogLevel.Warning);
                    builder.Services.AddSingleton(service);
                    builder.Services.AddMcpServer(o => o.ServerInfo = new() { Name = AppInfo.CommandName, Version = AppInfo.Version })
                        .WithStdioServerTransport().WithTools<DecisionTools>(serializerOptions: JsonData.Options);
                    using (var host = builder.Build()) await host.RunAsync(cancellation.Token);
                    return 0;
                default:
                    throw new DecisionException($"Unknown command '{cli.Command}'. Run with --help.");
            }
        }
        catch (OperationCanceledException) { Console.Error.WriteLine("Cancelled."); return 130; }
        catch (Exception e) when (e is DecisionException or JsonException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            Console.Error.WriteLine($"Error: {e.Message}");
            return 1;
        }
        finally { Console.CancelKeyPress -= handler; }
    }

    private static async Task<DecisionRequest> ReadInputAsync(CliArguments cli, CancellationToken cancellationToken)
    {
        if (cli.Get("input") is { } path)
        {
            if (new[] { "text", "question", "choice", "level", "name" }.Any(cli.Has))
                throw new DecisionException("Use --input or the --text/--question shorthand, not both.");
            string json = path == "-" ? await Console.In.ReadToEndAsync(cancellationToken) : await File.ReadAllTextAsync(path, cancellationToken);
            return JsonData.ReadRequest(json);
        }
        if (cli.Get("text") is not { } text || cli.Get("question") is not { } question)
            throw new DecisionException("Supply --input <JSON file|-> or --text and --question.");
        var choices = cli.All("choice");
        var levels = cli.All("level");
        if (choices.Count > 0 && levels.Count > 0) throw new DecisionException("Use either --choice or --level.");
        DecisionQuestion q = new PredicateQuestion(question);
        if (choices.Count > 0)
        {
            Dictionary<string, string> values = new(StringComparer.Ordinal);
            foreach (string choice in choices)
            {
                int equals = choice.IndexOf('=');
                if (equals <= 0 || !values.TryAdd(choice[..equals], choice[(equals + 1)..]))
                    throw new DecisionException("Each --choice must be a distinct label=description.");
            }
            q = new ChoiceQuestion(question, values);
        }
        else if (levels.Count > 0) q = new ScoreQuestion(question, levels.ToArray());
        return new(JsonSerializer.SerializeToElement(text), new(StringComparer.Ordinal) { [cli.Get("name") ?? "decision"] = q });
    }

    private static async Task<int> LoginAsync(ProviderOptions options, CancellationToken cancellationToken)
    {
        if (options.Provider != "copilot" || options.Auth is not ("auto" or "copilot-cli" or "gh"))
            throw new DecisionException("login supports --provider copilot with --auth copilot-cli or gh. For Foundry, run 'az login'.");
        string executable = options.Auth == "gh" ? "gh" : "copilot";
        var start = new ProcessStartInfo(executable) { UseShellExecute = false };
        foreach (string argument in executable == "gh" ? new[] { "auth", "login", "--web", "--hostname", "github.com" } : new[] { "login" })
            start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        try { process.Start(); }
        catch (Win32Exception) { throw new DecisionException($"Install {executable} first, or use 'login --auth gh'."); }
        try { await process.WaitForExitAsync(cancellationToken); }
        catch (OperationCanceledException) { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        return process.ExitCode;
    }

    private const string Help = """
        gh-decide - experimental decision-model CLI and MCP server

        gh-decide <command> [options]
        dotnet run --file Decision.cs -- <command> [options]

        Commands:
          decide   Evaluate --input <JSON file|->, or --text TEXT --question TEXT.
          models   Discover Copilot decision models / describe a configured model.
          mcp      Run an MCP stdio server (stdout is reserved for MCP).
          login    Reuse browser login through copilot (default) or gh.
          version  Print the utility version (also --version or -v).

        Provider options (also usable with mcp):
          --provider copilot|foundry|openai       Default: copilot
          --transport auto|sdk|http               Default: SDK for Copilot, HTTP otherwise
          --copilot-cli-path PATH                 Optional compatible SDK runtime executable
          --model ID                             Optional model / MCP default; Foundry: deployment name
          --default-model ID                     Alias for --model; sets the MCP startup default
          --endpoint HTTPS_RESOURCE_ROOT         No API path
          --protocol auto|systemone|decisions     Copilot discovers it; Foundry requires it
          --auth auto|copilot-cli|gh|env          Copilot authentication
                 auto|azure-cli|entra|api-key     Foundry authentication
                 auto|api-key                    OpenAI authentication
          --account GITHUB_LOGIN                 Select a saved github.com login
          --copilot-config PATH                  Default: ~/.copilot/config.json
          --api-key-env NAME                     Override the API key variable name
          --timeout SECONDS                      Total budget: 60 by default; SDK RPC maximum: 30s
          --config FILE --profile NAME           Read named configuration

        Decision options:
          --input FILE|-                         Common request JSON
          --text TEXT --question TEXT            One predicate (yes/no probability)
          --choice label=description             Repeat to ask a choice question
          --level DESCRIPTION                    Repeat, lowest first, for a score
          --name NAME                            Shorthand question name (default: decision)
          --raw                                  HTTP: provider JSON; SDK: normalized RPC JSON

        Examples:
          dotnet run --file Decision.cs -- models
          dotnet run --file Decision.cs -- decide --input examples/ticket.json
          dotnet run --file Decision.cs -- decide --model gpt-6-luna-decisions --input examples/ticket.json
          dotnet run --file Decision.cs -- mcp --auth copilot-cli
          dotnet run --file Decision.cs -- mcp --default-model gpt-6-luna-decisions
          dotnet run --file Decision.cs -- decide --transport http --input examples/ticket.json

        MCP tool calls may omit model: call override > configured default > microsoft-decision-1 (Copilot).
        Configuration: CLI > DECISION_* environment > selected profile > defaults.
        See docs/USAGE.md and RESEARCH.md for authentication, protocols, and evidence.
        """;
}

public sealed class CliArguments
{
    public string Command { get; private set; } = "help";
    private readonly Dictionary<string, List<string>> values = new(StringComparer.Ordinal);
    public bool Has(string name) => values.ContainsKey(name);
    public string? Get(string name) => values.TryGetValue(name, out var list) ? list[^1] : null;
    public IReadOnlyList<string> All(string name) => values.TryGetValue(name, out var list) ? list : [];

    public static CliArguments Parse(string[] args)
    {
        CliArguments result = new();
        HashSet<string> flags = [with(StringComparer.Ordinal), "help", "raw"];
        HashSet<string> valued = [with(StringComparer.Ordinal), "provider", "transport", "copilot-cli-path", "model", "default-model", "endpoint", "protocol", "auth", "account", "copilot-config", "api-key-env",
            "timeout", "config", "profile", "input", "text", "question", "choice", "level", "name"];
        bool commandSeen = false;
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i] == "-h" ? "--help" : args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                if (commandSeen) throw new DecisionException($"Unexpected argument '{arg}'.");
                result.Command = arg;
                commandSeen = true;
                continue;
            }
            string[] parts = arg[2..].Split('=', 2);
            string key = parts[0];
            if (!flags.Contains(key) && !valued.Contains(key)) throw new DecisionException($"Unknown option '--{key}'.");
            if (key == "default-model") key = "model";
            if (result.Has(key) && key is not ("choice" or "level")) throw new DecisionException($"Option '--{key}' was supplied more than once.");
            string value = "true";
            if (flags.Contains(key) && parts.Length > 1) throw new DecisionException($"--{key} does not take a value.");
            if (valued.Contains(key))
            {
                if (parts.Length > 1) value = parts[1];
                else if (++i < args.Length && !args[i].StartsWith("--", StringComparison.Ordinal)) value = args[i];
                else throw new DecisionException($"--{key} requires a value.");
            }
            if (!result.values.TryGetValue(key, out var list)) result.values[key] = list = [];
            list.Add(value);
        }
        if (result.Command is not ("help" or "version" or "decide" or "models" or "mcp" or "login"))
            throw new DecisionException($"Unknown command '{result.Command}'.");
        if (result.Command != "decide" && new[] { "input", "text", "question", "choice", "level", "name", "raw" }.Any(result.Has))
            throw new DecisionException("Decision input options can only be used with the decide command.");
        return result;
    }
}

public static class Configuration
{
    public static ProviderOptions Load(CliArguments cli)
    {
        string? file = cli.Get("config") ?? Environment.GetEnvironmentVariable("DECISION_CONFIG");
        string? profile = cli.Get("profile") ?? Environment.GetEnvironmentVariable("DECISION_PROFILE");
        JsonObject merged = [];
        if (file is not null)
        {
            var config = JsonNode.Parse(File.ReadAllText(file)) as JsonObject ?? throw new DecisionException("Configuration must be a JSON object.");
            profile ??= config["defaultProfile"]?.GetValue<string>();
            if (profile is null || config["profiles"]?[profile] is not JsonObject selected)
                throw new DecisionException("Select an existing profile with --profile or defaultProfile in the configuration file.");
            merged = (JsonObject)selected.DeepClone();
        }
        else if (profile is not null) throw new DecisionException("--profile requires --config (or DECISION_CONFIG).");

        foreach (var (option, property, environment) in new (string, string, string)[]
        {
            ("provider", "provider", "DECISION_PROVIDER"), ("model", "model", "DECISION_MODEL"),
            ("transport", "transport", "DECISION_TRANSPORT"), ("copilot-cli-path", "copilotCliPath", "DECISION_COPILOT_CLI_PATH"),
            ("endpoint", "endpoint", "DECISION_ENDPOINT"), ("protocol", "protocol", "DECISION_PROTOCOL"),
            ("auth", "auth", "DECISION_AUTH"), ("account", "account", "DECISION_ACCOUNT"),
            ("copilot-config", "copilotConfig", "DECISION_COPILOT_CONFIG"), ("api-key-env", "apiKeyEnv", "DECISION_API_KEY_ENV")
        })
        {
            string? value = cli.Get(option) ?? Environment.GetEnvironmentVariable(environment);
            if (value is not null) merged[property] = value;
        }
        string? timeout = cli.Get("timeout") ?? Environment.GetEnvironmentVariable("DECISION_TIMEOUT_SECONDS");
        if (timeout is not null)
        {
            if (!int.TryParse(timeout, NumberStyles.None, CultureInfo.InvariantCulture, out int seconds))
                throw new DecisionException("--timeout / DECISION_TIMEOUT_SECONDS must be an integer.");
            merged["timeoutSeconds"] = seconds;
        }
        var options = merged.Deserialize<ProviderOptions>(JsonData.Options) ?? new();
        options = options with { Provider = options.Provider.ToLowerInvariant(), Protocol = options.Protocol.ToLowerInvariant(), Auth = options.Auth.ToLowerInvariant(), Transport = options.Transport.ToLowerInvariant() };
        options.Validate();
        return options;
    }
}
