using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ari61850Bridge.Protocol.Acse;
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
    public string LastReadResponseHex { get; private set; } = string.Empty;
    public IReadOnlyList<MmsReadAttempt> LastReadAttempts { get; private set; } = Array.Empty<MmsReadAttempt>();
    public string LastReadAttemptSummary => LastReadAttempts.Count == 0
        ? string.Empty
        : string.Join(" | ", LastReadAttempts.Select(a => a.Summary));
    private int _nextInvokeId = 1;

    public async Task ConnectAsync(string ipAddress, int port, CancellationToken cancellationToken)
    {
        State = NativeIec61850AssociationState.Disconnected;
        LastHandshakeMessage = string.Empty;
        LastAssociationResponseHex = string.Empty;
        LastAssociationAttempts = Array.Empty<AcseAssociationAttempt>();
        LastReadResponseHex = string.Empty;
        LastReadAttempts = Array.Empty<MmsReadAttempt>();
        _nextInvokeId = 1;

        var attempts = new List<AcseAssociationAttempt>();
        Exception? lastTransportException = null;

        foreach (var profile in AcseMmsInitiateRequest.BuildAssociationProfiles())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ResetTransportAsync().ConfigureAwait(false);

            try
            {
                await _tpkt.ConnectAsync(ipAddress, port, cancellationToken).ConfigureAwait(false);
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

    public async Task<MmsReadDecodeResult> ReadSingleVariableAsync(MmsObjectReference reference, string dataTypeHint, CancellationToken cancellationToken)
    {
        if (!IsMmsInitiated)
            throw new InvalidOperationException($"Native IEC 61850 MMS association is not initiated. Current state: {State}.");

        var attempts = new List<MmsReadAttempt>();
        var candidates = BuildReadCandidates(reference);

        foreach (var (profile, candidate) in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var invokeId = NextInvokeId();
            var request = MmsReadRequest.BuildSingleVariableRead(invokeId, candidate);
            var response = await SendPresentationPayloadAsync(request, cancellationToken).ConfigureAwait(false);
            var result = MmsReadResponseDecoder.DecodeSingleVariable(response, dataTypeHint, invokeId);
            attempts.Add(new MmsReadAttempt { Profile = profile, Reference = candidate, Result = result });
            LastReadResponseHex = result.ResponseHexPreview;

            if (result.IsSuccess)
            {
                LastReadAttempts = attempts;
                LastHandshakeMessage = $"Native MMS Confirmed-Read succeeded using {profile}: {candidate}. {result.Message}";
                return result;
            }

            if (!ShouldTryAlternateRead(candidate, result))
                break;
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

    private static bool ShouldTryAlternateRead(MmsObjectReference candidate, MmsReadDecodeResult result)
    {
        if (result.IsSuccess) return false;
        var message = result.Message ?? string.Empty;
        return message.Contains("object", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("access", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Confirmed-Error", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Reject", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("not yet recognize", StringComparison.OrdinalIgnoreCase);
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
