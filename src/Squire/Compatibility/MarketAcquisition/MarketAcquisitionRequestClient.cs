using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MarketMafioso.MarketAcquisition;

public interface IMarketAcquisitionRequestClient
{
    Task<MarketAcquisitionRequestView> GetBatchAsync(
        string serverUrl,
        string clientApiKey,
        string requestId,
        CancellationToken cancellationToken);

    Task<MarketAcquisitionRequestView> CreateBatchAsync(
        string serverUrl,
        string clientApiKey,
        MarketAcquisitionBatchCreateRequest createRequest,
        CancellationToken cancellationToken);

    Task<MarketAcquisitionRequestView> ReplaceBatchAsync(
        string serverUrl,
        string clientApiKey,
        string requestId,
        MarketAcquisitionBatchReplaceRequest replaceRequest,
        CancellationToken cancellationToken);

    Task<MarketAcquisitionClaimView> ClaimAsync(
        string serverUrl,
        string clientApiKey,
        string requestId,
        string characterName,
        string world,
        string pluginInstanceId,
        CancellationToken cancellationToken);

    Task<MarketAcquisitionRequestView> AcceptAsync(
        string serverUrl,
        string clientApiKey,
        string requestId,
        string claimToken,
        string idempotencyKey,
        CancellationToken cancellationToken);
}

public sealed class MarketAcquisitionRequestClient : IMarketAcquisitionRequestClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient httpClient;

    public MarketAcquisitionRequestClient(HttpClient httpClient)
    {
        this.httpClient = httpClient;
    }

    public async Task<MarketAcquisitionRequestView> GetBatchAsync(
        string serverUrl,
        string clientApiKey,
        string requestId,
        CancellationToken cancellationToken)
    {
        using var request = CreateAuthenticatedRequest(
            HttpMethod.Get,
            $"{ResolveAcquisitionBaseUrl(serverUrl)}/batches/{Uri.EscapeDataString(requestId)}",
            clientApiKey);
        return await SendJsonAsync<MarketAcquisitionRequestView>(request, "Get batch", cancellationToken).ConfigureAwait(false);
    }

    public async Task<MarketAcquisitionRequestView> CreateBatchAsync(
        string serverUrl,
        string clientApiKey,
        MarketAcquisitionBatchCreateRequest createRequest,
        CancellationToken cancellationToken)
    {
        using var request = CreateAuthenticatedRequest(
            HttpMethod.Post,
            $"{ResolveAcquisitionBaseUrl(serverUrl)}/batches",
            clientApiKey,
            createRequest);
        return await SendJsonAsync<MarketAcquisitionRequestView>(request, "Create batch", cancellationToken).ConfigureAwait(false);
    }

    public async Task<MarketAcquisitionRequestView> ReplaceBatchAsync(
        string serverUrl,
        string clientApiKey,
        string requestId,
        MarketAcquisitionBatchReplaceRequest replaceRequest,
        CancellationToken cancellationToken)
    {
        using var request = CreateAuthenticatedRequest(
            HttpMethod.Put,
            $"{ResolveAcquisitionBaseUrl(serverUrl)}/batches/{Uri.EscapeDataString(requestId)}",
            clientApiKey,
            replaceRequest);
        return await SendJsonAsync<MarketAcquisitionRequestView>(request, "Replace batch", cancellationToken, "replace").ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MarketAcquisitionRequestView>> FetchPendingAsync(
        string serverUrl,
        string clientApiKey,
        string characterName,
        string world,
        CancellationToken cancellationToken)
    {
        var url =
            $"{ResolveAcquisitionBaseUrl(serverUrl)}/batches/pending" +
            $"?characterName={Uri.EscapeDataString(characterName)}" +
            $"&world={Uri.EscapeDataString(world)}";
        using var request = CreateAuthenticatedRequest(HttpMethod.Get, url, clientApiKey);
        var pending = await SendJsonAsync<MarketAcquisitionBatchPendingResponse>(request, "Fetch pending batches", cancellationToken).ConfigureAwait(false);
        return pending.Batches;
    }

    public async Task<MarketAcquisitionClaimView> ClaimAsync(
        string serverUrl,
        string clientApiKey,
        string requestId,
        string characterName,
        string world,
        string pluginInstanceId,
        CancellationToken cancellationToken)
    {
        using var request = CreateAuthenticatedRequest(
            HttpMethod.Post,
            $"{ResolveAcquisitionBaseUrl(serverUrl)}/requests/{Uri.EscapeDataString(requestId)}/claim",
            clientApiKey,
            new MarketAcquisitionClaimRequest
            {
                CharacterName = characterName,
                World = world,
                PluginInstanceId = pluginInstanceId,
            });
        return await SendJsonAsync<MarketAcquisitionClaimView>(request, "Claim", cancellationToken).ConfigureAwait(false);
    }

    public Task<MarketAcquisitionRequestView> AcceptAsync(
        string serverUrl,
        string clientApiKey,
        string requestId,
        string claimToken,
        string idempotencyKey,
        CancellationToken cancellationToken) =>
        PostLifecycleAsync(
            serverUrl,
            clientApiKey,
            requestId,
            "accept",
            new MarketAcquisitionClaimTokenRequest
            {
                ClaimToken = claimToken,
                IdempotencyKey = idempotencyKey,
            },
            cancellationToken);

    public async Task<MarketAcquisitionExecutionLeaseView> RenewLeaseAsync(
        string serverUrl,
        string clientApiKey,
        string requestId,
        string claimToken,
        string pluginInstanceId,
        CancellationToken cancellationToken)
    {
        using var request = CreateAuthenticatedRequest(
            HttpMethod.Post,
            $"{ResolveAcquisitionBaseUrl(serverUrl)}/work-orders/{Uri.EscapeDataString(requestId)}/lease/renew",
            clientApiKey,
            new MarketAcquisitionLeaseRenewRequest
            {
                ClaimToken = claimToken,
                PluginInstanceId = pluginInstanceId,
            });
        return await SendJsonAsync<MarketAcquisitionExecutionLeaseView>(request, "Lease renewal", cancellationToken).ConfigureAwait(false);
    }

    public Task<MarketAcquisitionRequestView> RejectAsync(
        string serverUrl,
        string clientApiKey,
        string requestId,
        string claimToken,
        string idempotencyKey,
        string reason,
        CancellationToken cancellationToken) =>
        PostLifecycleAsync(
            serverUrl,
            clientApiKey,
            requestId,
            "reject",
            new MarketAcquisitionLifecycleRequest
            {
                ClaimToken = claimToken,
                IdempotencyKey = idempotencyKey,
                Reason = reason,
            },
            cancellationToken);

    public Task<MarketAcquisitionRequestView> ReportProgressAsync(
        string serverUrl,
        string clientApiKey,
        string requestId,
        string claimToken,
        string idempotencyKey,
        string runnerState,
        string? message,
        CancellationToken cancellationToken) =>
        PostLifecycleAsync(
            serverUrl,
            clientApiKey,
            requestId,
            "progress",
            new MarketAcquisitionLifecycleRequest
            {
                ClaimToken = claimToken,
                IdempotencyKey = idempotencyKey,
                RunnerState = runnerState,
                Message = message,
            },
            cancellationToken);

    public Task<MarketAcquisitionRequestView> CompleteAsync(
        string serverUrl,
        string clientApiKey,
        string requestId,
        string claimToken,
        string idempotencyKey,
        string? message,
        CancellationToken cancellationToken) =>
        PostLifecycleAsync(
            serverUrl,
            clientApiKey,
            requestId,
            "complete",
            new MarketAcquisitionLifecycleRequest
            {
                ClaimToken = claimToken,
                IdempotencyKey = idempotencyKey,
                Message = message,
            },
            cancellationToken);

    public Task<MarketAcquisitionRequestView> FailAsync(
        string serverUrl,
        string clientApiKey,
        string requestId,
        string claimToken,
        string idempotencyKey,
        string reason,
        CancellationToken cancellationToken) =>
        PostLifecycleAsync(
            serverUrl,
            clientApiKey,
            requestId,
            "fail",
            new MarketAcquisitionLifecycleRequest
            {
                ClaimToken = claimToken,
                IdempotencyKey = idempotencyKey,
                Reason = reason,
            },
            cancellationToken);

    public Task<MarketAcquisitionAttemptEventResult> ReportAttemptProgressAsync(
        string serverUrl,
        string clientApiKey,
        string requestId,
        string claimToken,
        string pluginInstanceId,
        string attemptId,
        long eventSequence,
        string? routeStopId,
        string? worldName,
        string phase,
        string? message,
        string? pluginVersion,
        CancellationToken cancellationToken) =>
        PostAttemptLifecycleAsync(
            serverUrl,
            clientApiKey,
            requestId,
            "progress",
            CreateAttemptEventRequest(
                claimToken,
                pluginInstanceId,
                attemptId,
                eventSequence,
                "progress",
                phase,
                routeStopId,
                worldName,
                phase,
                message,
                reason: null,
                pluginVersion),
            cancellationToken);

    public Task<MarketAcquisitionAttemptEventResult> CompleteAttemptAsync(
        string serverUrl,
        string clientApiKey,
        string requestId,
        string claimToken,
        string pluginInstanceId,
        string attemptId,
        long eventSequence,
        string? routeStopId,
        string? worldName,
        string phase,
        string? message,
        string? pluginVersion,
        CancellationToken cancellationToken) =>
        PostAttemptLifecycleAsync(
            serverUrl,
            clientApiKey,
            requestId,
            "complete",
            CreateAttemptEventRequest(
                claimToken,
                pluginInstanceId,
                attemptId,
                eventSequence,
                "complete",
                phase,
                routeStopId,
                worldName,
                phase,
                message,
                reason: null,
                pluginVersion),
            cancellationToken);

    public Task<MarketAcquisitionAttemptEventResult> FailAttemptAsync(
        string serverUrl,
        string clientApiKey,
        string requestId,
        string claimToken,
        string pluginInstanceId,
        string attemptId,
        long eventSequence,
        string? routeStopId,
        string? worldName,
        string phase,
        string reason,
        string? pluginVersion,
        CancellationToken cancellationToken) =>
        PostAttemptLifecycleAsync(
            serverUrl,
            clientApiKey,
            requestId,
            "fail",
            CreateAttemptEventRequest(
                claimToken,
                pluginInstanceId,
                attemptId,
                eventSequence,
                "fail",
                phase,
                routeStopId,
                worldName,
                phase,
                message: null,
                reason,
                pluginVersion),
            cancellationToken);

    public Task<MarketAcquisitionBatchLineView> PostLineProgressAsync(
        string serverUrl,
        string clientApiKey,
        string requestId,
        string lineId,
        MarketAcquisitionLineProgressRequest request,
        CancellationToken cancellationToken) =>
        PostBatchResourceAsync<MarketAcquisitionBatchLineView>(
            serverUrl,
            clientApiKey,
            requestId,
            $"lines/{Uri.EscapeDataString(lineId)}/progress",
            request,
            "line progress",
            cancellationToken);

    public Task<MarketAcquisitionPurchaseAuditView> PostPurchaseAuditAsync(
        string serverUrl,
        string clientApiKey,
        string requestId,
        MarketAcquisitionPurchaseAuditRequest request,
        CancellationToken cancellationToken) =>
        PostBatchResourceAsync<MarketAcquisitionPurchaseAuditView>(
            serverUrl,
            clientApiKey,
            requestId,
            "purchases",
            request,
            "purchase audit",
            cancellationToken);

    public Task<MarketAcquisitionMarketObservationView> PostMarketObservationAsync(
        string serverUrl,
        string clientApiKey,
        string requestId,
        MarketAcquisitionMarketObservationRequest request,
        CancellationToken cancellationToken) =>
        PostBatchResourceAsync<MarketAcquisitionMarketObservationView>(
            serverUrl,
            clientApiKey,
            requestId,
            "observations",
            request,
            "market observation",
            cancellationToken);

    public Task<MarketAcquisitionRequestView> ResendAsync(
        string serverUrl,
        string clientApiKey,
        string requestId,
        CancellationToken cancellationToken) =>
        PostLifecycleAsync(
            serverUrl,
            clientApiKey,
            requestId,
            "resend",
            new { },
            cancellationToken);

    private async Task<MarketAcquisitionRequestView> PostLifecycleAsync<TRequest>(
        string serverUrl,
        string clientApiKey,
        string requestId,
        string action,
        TRequest body,
        CancellationToken cancellationToken)
    {
        using var request = CreateAuthenticatedRequest(
            HttpMethod.Post,
            $"{ResolveAcquisitionBaseUrl(serverUrl)}/requests/{Uri.EscapeDataString(requestId)}/{action}",
            clientApiKey,
            body);
        return await SendJsonAsync<MarketAcquisitionRequestView>(request, "Lifecycle", cancellationToken, action).ConfigureAwait(false);
    }

    private async Task<TResponse> PostBatchResourceAsync<TResponse>(
        string serverUrl,
        string clientApiKey,
        string requestId,
        string relativePath,
        object body,
        string action,
        CancellationToken cancellationToken)
    {
        using var request = CreateAuthenticatedRequest(
            HttpMethod.Post,
            $"{ResolveAcquisitionBaseUrl(serverUrl)}/batches/{Uri.EscapeDataString(requestId)}/{relativePath}",
            clientApiKey,
            body);
        return await SendJsonAsync<TResponse>(request, action, cancellationToken, action).ConfigureAwait(false);
    }

    private async Task<MarketAcquisitionAttemptEventResult> PostAttemptLifecycleAsync(
        string serverUrl,
        string clientApiKey,
        string requestId,
        string action,
        MarketAcquisitionAttemptEventRequest body,
        CancellationToken cancellationToken)
    {
        using var request = CreateAuthenticatedRequest(
            HttpMethod.Post,
            $"{ResolveAcquisitionBaseUrl(serverUrl)}/requests/{Uri.EscapeDataString(requestId)}/{action}",
            clientApiKey,
            body);
        return await SendJsonAsync<MarketAcquisitionAttemptEventResult>(request, "Attempt lifecycle", cancellationToken, action).ConfigureAwait(false);
    }

    private static HttpRequestMessage CreateAuthenticatedRequest(
        HttpMethod method,
        string url,
        string clientApiKey,
        object? body = null)
    {
        if (string.IsNullOrWhiteSpace(clientApiKey))
            throw new InvalidOperationException("Client API key is required.");

        var request = new HttpRequestMessage(method, url);
        if (body is not null)
            request.Content = JsonContent.Create(body, options: JsonOptions);
        request.Headers.Add("X-Api-Key", clientApiKey);
        request.Headers.Accept.ParseAdd("application/json");
        return request;
    }

    private async Task<TResponse> SendJsonAsync<TResponse>(
        HttpRequestMessage request,
        string operation,
        CancellationToken cancellationToken,
        string? lifecycleAction = null)
    {
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            if (lifecycleAction is not null)
                throw await CreateLifecycleExceptionAsync(response, lifecycleAction, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
        }

        return await response.Content.ReadFromJsonAsync<TResponse>(JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"{operation} response was empty.");
    }

    private static MarketAcquisitionAttemptEventRequest CreateAttemptEventRequest(
        string claimToken,
        string pluginInstanceId,
        string attemptId,
        long eventSequence,
        string eventType,
        string phase,
        string? routeStopId,
        string? worldName,
        string? runnerState,
        string? message,
        string? reason,
        string? pluginVersion) =>
        new()
        {
            ClaimToken = claimToken,
            IdempotencyKey = $"{pluginInstanceId}-route-{attemptId}-{eventSequence}",
            PluginInstanceId = pluginInstanceId,
            AttemptId = attemptId,
            EventSequence = eventSequence,
            EventType = eventType,
            Phase = phase,
            RouteStopId = routeStopId,
            WorldName = worldName,
            RunnerState = runnerState,
            Message = message,
            Reason = reason,
            PluginVersion = pluginVersion,
            ClientTimestampUtc = DateTimeOffset.UtcNow,
        };

    private static async Task<MarketAcquisitionLifecycleHttpException> CreateLifecycleExceptionAsync(
        HttpResponseMessage response,
        string action,
        CancellationToken cancellationToken)
    {
        var body = response.Content == null
            ? null
            : await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var error = TryReadErrorMessage(body) ?? body;
        return new MarketAcquisitionLifecycleHttpException(response.StatusCode, action, error, body);
    }

    private static string? TryReadErrorMessage(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;

        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("error", out var error) &&
                   error.ValueKind == JsonValueKind.String
                ? error.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string ResolveAcquisitionBaseUrl(string serverUrl) =>
        ReceiverEndpointClassifier.BuildAcquisitionBaseUrl(serverUrl) ??
        throw new InvalidOperationException("The configured receiver URL cannot derive an acquisition endpoint.");
}

public sealed class MarketAcquisitionLifecycleHttpException : HttpRequestException
{
    public MarketAcquisitionLifecycleHttpException(
        HttpStatusCode statusCode,
        string action,
        string? error,
        string? responseBody)
        : base(BuildMessage(statusCode, action, error), null, statusCode)
    {
        Action = action;
        Error = error;
        ResponseBody = responseBody;
    }

    public string Action { get; }

    public string? Error { get; }

    public string? ResponseBody { get; }

    private static string BuildMessage(HttpStatusCode statusCode, string action, string? error) =>
        string.IsNullOrWhiteSpace(error)
            ? $"Market acquisition {action} failed with {(int)statusCode} {statusCode}."
            : $"Market acquisition {action} failed with {(int)statusCode} {statusCode}: {error}";
}
