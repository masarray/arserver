using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ari61850Bridge.Protocol.Acse;
using Ari61850Bridge.Protocol.Diagnostics;
using Ari61850Bridge.Protocol.Mms;
using Ari61850Bridge.Protocol.Osi;

namespace Ari61850Bridge.Protocol.Iec61850;

public enum NativeIec61850AssociationState
{
    Disconnected,
    TcpConnected,
    CotpConnected,
    AcsePending,
    MmsInitiated,
    MmsInitiateFailed
}

public sealed class NativeIec61850Session : IAsyncDisposable
{
    private readonly TpktClient _tpkt = new();
    private readonly CotpClient _cotp;
    private string _lastHost = string.Empty;
    private int _lastPort = 102;

    public NativeIec61850Session()
    {
        _cotp = new CotpClient(_tpkt);
    }

    public NativeIec61850AssociationState State { get; private set; } = NativeIec61850AssociationState.Disconnected;
    public bool IsTcpConnected => _tpkt.IsConnected;
    public bool IsTransportConnected => _tpkt.IsConnected && _cotp.IsConnected;
    public bool IsMmsInitiated => State == NativeIec61850AssociationState.MmsInitiated;
    public string LastHandshakeMessage { get; private set; } = string.Empty;
    public string LastAssociationResponseHex { get; private set; } = string.Empty;
    public IReadOnlyList<AcseAssociationAttempt> LastAssociationAttempts { get; private set; } = Array.Empty<AcseAssociationAttempt>();
    public string LastAssociationAttemptSummary => LastAssociationAttempts.Count == 0
        ? string.Empty
        : string.Join(" | ", LastAssociationAttempts.Select(a => a.Summary));
    public string LastReadRequestHex { get; private set; } = string.Empty;
    public string LastReadResponseHex { get; private set; } = string.Empty;
    public IReadOnlyList<MmsReadAttempt> LastReadAttempts { get; private set; } = Array.Empty<MmsReadAttempt>();
    public string LastReadAttemptSummary => LastReadAttempts.Count == 0
        ? string.Empty
        : string.Join(" | ", LastReadAttempts.Select(a => a.Summary));
    public string LastDiscoveryRequestHex { get; private set; } = string.Empty;
    public string LastDiscoveryResponseHex { get; private set; } = string.Empty;
    public string LastDiscoveryAttemptSummary { get; private set; } = string.Empty;
    private int _nextInvokeId = 1;

    public async Task ConnectAsync(string ipAddress, int port, CancellationToken cancellationToken)
    {
        _lastHost = ipAddress;
        _lastPort = port <= 0 ? 102 : port;
        _nextInvokeId = 1;
        LastReadRequestHex = string.Empty;
        LastReadResponseHex = string.Empty;
        LastReadAttempts = Array.Empty<MmsReadAttempt>();
        LastDiscoveryRequestHex = string.Empty;
        LastDiscoveryResponseHex = string.Empty;
        LastDiscoveryAttemptSummary = string.Empty;
        await AssociateAsync(resetAssociationDiagnostics: true, cancellationToken).ConfigureAwait(false);
    }

    private async Task AssociateAsync(bool resetAssociationDiagnostics, CancellationToken cancellationToken)
    {
        State = NativeIec61850AssociationState.Disconnected;
        if (resetAssociationDiagnostics)
        {
            LastHandshakeMessage = string.Empty;
            LastAssociationResponseHex = string.Empty;
            LastAssociationAttempts = Array.Empty<AcseAssociationAttempt>();
        }

        var attempts = new List<AcseAssociationAttempt>();
        Exception? lastTransportException = null;

        foreach (var profile in AcseMmsInitiateRequest.BuildAssociationProfiles())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ResetTransportAsync().ConfigureAwait(false);

            try
            {
                await _tpkt.ConnectAsync(_lastHost, _lastPort, cancellationToken).ConfigureAwait(false);
                State = NativeIec61850AssociationState.TcpConnected;

                await _cotp.ConnectAsync(cancellationToken).ConfigureAwait(false);
                State = NativeIec61850AssociationState.CotpConnected;
                LastHandshakeMessage = $"{profile.Name}: {_cotp.LastConnectionConfirm?.Message ?? "COTP connection confirmed."}";

                var result = await TryInitiateMmsAssociationAsync(profile, cancellationToken).ConfigureAwait(false);
                attempts.Add(new AcseAssociationAttempt
                {
                    ProfileName = profile.Name,
                    IsAccepted = result.IsAccepted,
                    Message = result.Message,
                    ResponseHexPreview = result.ResponseHexPreview
                });
                LastAssociationAttempts = attempts.ToArray();

                if (result.IsAccepted)
                {
                    State = NativeIec61850AssociationState.MmsInitiated;
                    LastHandshakeMessage = result.Message;
                    return;
                }

                State = NativeIec61850AssociationState.MmsInitiateFailed;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastTransportException = ex;
                State = NativeIec61850AssociationState.MmsInitiateFailed;
                attempts.Add(new AcseAssociationAttempt
                {
                    ProfileName = profile.Name,
                    IsAccepted = false,
                    Message = $"{profile.Name}: transport/association exception: {ex.GetType().Name}: {ex.Message}",
                    ResponseHexPreview = LastAssociationResponseHex
                });
                LastAssociationAttempts = attempts.ToArray();
            }
        }

        await ResetTransportAsync().ConfigureAwait(false);
        State = NativeIec61850AssociationState.MmsInitiateFailed;
        LastHandshakeMessage = LastAssociationAttemptSummary;
        if (string.IsNullOrWhiteSpace(LastHandshakeMessage) && lastTransportException != null)
            LastHandshakeMessage = $"Native ACSE/MMS association failed: {lastTransportException.GetType().Name}: {lastTransportException.Message}";
    }

    public async Task<AcseMmsInitiateResult> TryInitiateMmsAssociationAsync(AcseAssociationProfile profile, CancellationToken cancellationToken)
    {
        if (!IsTransportConnected)
            throw new InvalidOperationException("Native IEC 61850 transport is not connected.");

        State = NativeIec61850AssociationState.AcsePending;
        await _cotp.SendDataAsync(profile.Payload, cancellationToken).ConfigureAwait(false);
        var response = await _cotp.ReceiveDataAsync(cancellationToken).ConfigureAwait(false);
        var result = AcseMmsInitiateResult.Parse(response, profile.Name);
        LastAssociationResponseHex = result.ResponseHexPreview;
        LastHandshakeMessage = result.Message;
        return result;
    }

    public Task<AcseMmsInitiateResult> TryInitiateMmsAssociationAsync(CancellationToken cancellationToken)
    {
        var profile = AcseMmsInitiateRequest.BuildAssociationProfiles()[0];
        return TryInitiateMmsAssociationAsync(profile, cancellationToken);
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> DiscoverDomainVariableNamesAsync(CancellationToken cancellationToken)
    {
        if (!IsMmsInitiated)
            throw new InvalidOperationException($"Native IEC 61850 MMS association is not initiated. Current state: {State}.");

        var summary = new List<string>();
        var domainsResult = await GetNameListPagedAsync(MmsGetNameListObjectClass.Domain, null, cancellationToken).ConfigureAwait(false);
        if (!domainsResult.IsSuccess)
        {
            LastDiscoveryAttemptSummary = $"Domain GetNameList failed: {domainsResult.Message}";
            return new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        }

        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var domains = domainsResult.Names
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .Take(256)
            .ToList();

        summary.Add($"LD/domain={domains.Count}");

        foreach (var domain in domains)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var variables = await GetNameListPagedAsync(MmsGetNameListObjectClass.NamedVariable, domain, cancellationToken).ConfigureAwait(false);
            if (variables.IsSuccess)
            {
                result[domain] = variables.Names
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                    .Take(20000)
                    .ToList();
                summary.Add($"{domain}:var={result[domain].Count}");
            }
            else
            {
                result[domain] = Array.Empty<string>();
                summary.Add($"{domain}:var=failed:{variables.Message}");
            }
        }

        LastDiscoveryAttemptSummary = "Native GetNameList discovery: " + string.Join(" | ", summary.Take(20));
        return result;
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> DiscoverDomainVariableListNamesAsync(CancellationToken cancellationToken)
    {
        if (!IsMmsInitiated)
            throw new InvalidOperationException($"Native IEC 61850 MMS association is not initiated. Current state: {State}.");

        var domainsResult = await GetNameListPagedAsync(MmsGetNameListObjectClass.Domain, null, cancellationToken).ConfigureAwait(false);
        if (!domainsResult.IsSuccess)
            return new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var domain in domainsResult.Names.Take(256))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var lists = await GetNameListPagedAsync(MmsGetNameListObjectClass.NamedVariableList, domain, cancellationToken).ConfigureAwait(false);
            result[domain] = lists.IsSuccess
                ? lists.Names.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList()
                : (IReadOnlyList<string>)Array.Empty<string>();
        }
        return result;
    }

    public async Task<MmsNameListResult> GetNameListPagedAsync(MmsGetNameListObjectClass objectClass, string? domainId, CancellationToken cancellationToken)
    {
        var names = new List<string>();
        var continueAfter = string.Empty;
        var page = 0;
        MmsNameListResult? last = null;

        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsMmsInitiated)
            {
                var recovered = await TryRecoverAssociationAsync(cancellationToken).ConfigureAwait(false);
                if (!recovered)
                {
                    return new MmsNameListResult
                    {
                        IsSuccess = false,
                        Names = names,
                        MoreFollows = false,
                        Message = $"Native MMS association is not available for GetNameList {objectClass}/{domainId}. State={State}."
                    };
                }
            }

            page++;
            var invokeId = NextInvokeId();
            var request = MmsGetNameListRequest.Build(invokeId, objectClass, domainId, string.IsNullOrWhiteSpace(continueAfter) ? null : continueAfter);
            LastDiscoveryRequestHex = HexDump.ToCompactString(request);

            try
            {
                var response = await SendPresentationPayloadAsync(request, cancellationToken).ConfigureAwait(false);
                last = MmsGetNameListResponseDecoder.Decode(response, invokeId);
                LastDiscoveryResponseHex = last.ResponseHexPreview;

                if (!last.IsSuccess)
                {
                    LastDiscoveryAttemptSummary = $"GetNameList {objectClass}/{domainId ?? "VMD"} page {page} failed: {last.Message}";
                    return new MmsNameListResult
                    {
                        IsSuccess = false,
                        Names = names,
                        MoreFollows = false,
                        Message = last.Message,
                        ResponseHexPreview = last.ResponseHexPreview
                    };
                }

                foreach (var name in last.Names)
                    if (!names.Contains(name, StringComparer.OrdinalIgnoreCase)) names.Add(name);

                continueAfter = last.Names.LastOrDefault() ?? continueAfter;
                LastDiscoveryAttemptSummary = $"GetNameList {objectClass}/{domainId ?? "VMD"}: page={page}, total={names.Count}, more={last.MoreFollows}.";
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ObjectDisposedException or InvalidOperationException)
            {
                await MarkProtocolFaultAsync().ConfigureAwait(false);
                LastDiscoveryAttemptSummary = $"GetNameList {objectClass}/{domainId ?? "VMD"} transport fault on page {page}: {ex.GetType().Name}: {ex.Message}";
                return new MmsNameListResult
                {
                    IsSuccess = false,
                    Names = names,
                    MoreFollows = false,
                    Message = LastDiscoveryAttemptSummary,
                    ResponseHexPreview = LastDiscoveryResponseHex
                };
            }
        }
        while (last.MoreFollows && page < 64 && !string.IsNullOrWhiteSpace(continueAfter));

        return new MmsNameListResult
        {
            IsSuccess = true,
            Names = names,
            MoreFollows = last?.MoreFollows ?? false,
            Message = $"GetNameList {objectClass}/{domainId ?? "VMD"} completed: {names.Count} name(s), pages={page}.",
            ResponseHexPreview = last?.ResponseHexPreview ?? string.Empty
        };
    }

    public async Task<MmsReadDecodeResult> ReadSingleVariableAsync(MmsObjectReference reference, string dataTypeHint, CancellationToken cancellationToken)
    {
        if (!IsMmsInitiated)
            throw new InvalidOperationException($"Native IEC 61850 MMS association is not initiated. Current state: {State}.");

        var attempts = new List<MmsReadAttempt>();
        var candidates = BuildReadCandidates(reference);
        var payloadProfiles = BuildPayloadProfiles();

        foreach (var (objectProfile, candidate) in candidates)
        {
            foreach (var payloadProfile in payloadProfiles)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!IsMmsInitiated)
                {
                    var recovered = await TryRecoverAssociationAsync(cancellationToken).ConfigureAwait(false);
                    if (!recovered)
                        break;
                }

                var invokeId = NextInvokeId();
                var request = MmsReadRequest.BuildSingleVariableRead(invokeId, candidate, payloadProfile);
                var requestHex = HexDump.ToCompactString(request);
                LastReadRequestHex = requestHex;

                MmsReadDecodeResult result;
                try
                {
                    var response = await SendPresentationPayloadAsync(request, cancellationToken).ConfigureAwait(false);
                    result = MmsReadResponseDecoder.DecodeSingleVariable(response, dataTypeHint, invokeId);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or ObjectDisposedException or InvalidOperationException)
                {
                    result = new MmsReadDecodeResult
                    {
                        IsSuccess = false,
                        Message = $"Native MMS read transport fault after {payloadProfile}: {ex.GetType().Name}: {ex.Message}",
                        ResponseHexPreview = LastReadResponseHex
                    };
                    await MarkProtocolFaultAsync().ConfigureAwait(false);
                }

                attempts.Add(new MmsReadAttempt
                {
                    Profile = objectProfile,
                    PayloadProfile = payloadProfile,
                    Reference = candidate,
                    RequestHexPreview = requestHex,
                    Result = result
                });
                LastReadAttempts = attempts.ToArray();
                LastReadResponseHex = result.ResponseHexPreview;

                if (result.IsSuccess)
                {
                    LastHandshakeMessage = $"Native MMS Confirmed-Read succeeded using {objectProfile}/{payloadProfile}: {candidate}. {result.Message}";
                    return result;
                }

                if (!ShouldTryNextPayloadProfile(result))
                    break;
            }
        }

        LastReadAttempts = attempts;
        var last = attempts.LastOrDefault()?.Result ?? new MmsReadDecodeResult
        {
            IsSuccess = false,
            Message = "Native MMS Confirmed-Read did not return a decodable value.",
            ResponseHexPreview = LastReadResponseHex
        };
        LastHandshakeMessage = LastReadAttemptSummary;
        return last;
    }

    private static IReadOnlyList<(MmsReadProfile Profile, MmsObjectReference Reference)> BuildReadCandidates(MmsObjectReference reference)
    {
        var candidates = new List<(MmsReadProfile, MmsObjectReference)>
        {
            (MmsReadProfile.PrimaryFcNamedVariable, reference)
        };

        var noFc = reference.WithoutFunctionalConstraint();
        if (!string.Equals(noFc.Item, reference.Item, StringComparison.OrdinalIgnoreCase))
            candidates.Add((MmsReadProfile.AlternateNoFcNamedVariable, noFc));

        return candidates;
    }

    private static IReadOnlyList<MmsReadPayloadProfile> BuildPayloadProfiles() => new[]
    {
        MmsReadPayloadProfile.PresentationDataValues,
        MmsReadPayloadProfile.PresentationDataValuesWithSpecificationResult,
        MmsReadPayloadProfile.SessionDataOnly,
        MmsReadPayloadProfile.RawMmsPdu
    };

    private static bool ShouldTryNextPayloadProfile(MmsReadDecodeResult result)
    {
        if (result.IsSuccess) return false;
        var message = result.Message ?? string.Empty;

        // Access/object failures mean the envelope was understood; move to the next object-name profile.
        if (message.Contains("AccessResult.failure", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("object", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("access", StringComparison.OrdinalIgnoreCase))
            return false;

        // Presentation/profile/parser failures can be explored with the next native payload profile.
        return message.Contains("transport fault", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Expected MMS Confirmed", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Reject", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Abort", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("decode failed", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("does not yet recognize", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> TryRecoverAssociationAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_lastHost))
            return false;

        try
        {
            await AssociateAsync(resetAssociationDiagnostics: false, cancellationToken).ConfigureAwait(false);
            return IsMmsInitiated;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LastHandshakeMessage = $"Native MMS read recovery association failed: {ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    private int NextInvokeId()
    {
        var invokeId = Interlocked.Increment(ref _nextInvokeId);
        if (invokeId > 0x7FFF)
        {
            Interlocked.Exchange(ref _nextInvokeId, 1);
            invokeId = 1;
        }
        return invokeId;
    }

    public Task<byte[]> SendPresentationPayloadAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        if (!IsTransportConnected)
            throw new InvalidOperationException("Native IEC 61850 transport is not connected.");

        return SendAndReceiveAsync(payload, cancellationToken);
    }

    private async Task<byte[]> SendAndReceiveAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        await _cotp.SendDataAsync(payload, cancellationToken).ConfigureAwait(false);
        return await _cotp.ReceiveDataAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task MarkProtocolFaultAsync()
    {
        State = NativeIec61850AssociationState.MmsInitiateFailed;
        await ResetTransportAsync().ConfigureAwait(false);
    }

    private async ValueTask ResetTransportAsync()
    {
        _cotp.Reset();
        await _tpkt.DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        State = NativeIec61850AssociationState.Disconnected;
        await ResetTransportAsync().ConfigureAwait(false);
    }
}
