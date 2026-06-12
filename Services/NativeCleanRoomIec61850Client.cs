using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Ari61850Bridge.Models;
using Ari61850Bridge.Protocol.Iec61850;
using Ari61850Bridge.Protocol.Mms;

namespace Ari61850Bridge.Services;

/// <summary>
/// Clear-room IEC 61850 MMS client foundation.
///
/// This class intentionally does not reuse libiec61850, wrappers, generated bindings,
/// or translated GPL implementation details. It starts as a native TCP/TPKT/COTP/ACSE/MMS
/// boundary and will be expanded phase-by-phase from SCL-driven reads to reporting.
/// </summary>
public sealed class NativeCleanRoomIec61850Client : IIec61850Client
{
    private readonly NativeIec61850Session _session = new();

    public bool IsConnected => _session.IsMmsInitiated;
    public bool IsTransportReady => _session.IsTransportConnected;
    public bool IsMmsReady => _session.IsMmsInitiated;
    public bool IsMmsInitiateFailed => _session.State == NativeIec61850AssociationState.MmsInitiateFailed;
    public string NativeState => _session.State.ToString();
    public string ConnectionMode => "Native Clean-Room IEC 61850 MMS Preview";
    public string LastErrorMessage { get; private set; } = string.Empty;
    public string LastAssociationResponseHex => _session.LastAssociationResponseHex;
    public string LastAssociationAttemptSummary => _session.LastAssociationAttemptSummary;
    public string LastReadResponseHex => _session.LastReadResponseHex;
    public string LastReadAttemptSummary => _session.LastReadAttemptSummary;

    public async Task ConnectAsync(string ipAddress, int port, CancellationToken cancellationToken)
    {
        LastErrorMessage = string.Empty;
        try
        {
            await _session.ConnectAsync(ipAddress, port <= 0 ? 102 : port, cancellationToken).ConfigureAwait(false);
            LastErrorMessage = string.IsNullOrWhiteSpace(_session.LastAssociationAttemptSummary)
                ? _session.LastHandshakeMessage
                : _session.LastAssociationAttemptSummary;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LastErrorMessage = $"Native clean-room TCP/TPKT/COTP/ACSE preflight failed for {ipAddress}:{port}. {ex.GetType().Name}: {ex.Message}";
            await _session.DisposeAsync().ConfigureAwait(false);
        }
    }

    public Task<IReadOnlyList<SignalDefinition>> DiscoverSignalsAsync(CancellationToken cancellationToken)
    {
        // Online discovery remains intentionally out of this clean-room phase.
        // The first native path is SCL-driven: Open SCL -> choose signal -> start session -> polling/report planner.
        IReadOnlyList<SignalDefinition> empty = Array.Empty<SignalDefinition>();
        return Task.FromResult(empty);
    }

    public Task<object?> ReadValueAsync(string objectReference, CancellationToken cancellationToken)
    {
        return ReadValueAsync(objectReference, string.Empty, string.Empty, cancellationToken);
    }

    public async Task<object?> ReadValueAsync(string objectReference, string functionalConstraint, string dataType, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_session.IsTransportConnected)
        {
            LastErrorMessage = "Native IEC 61850 transport is not connected. Start session again after TCP/COTP preflight succeeds.";
            return null;
        }

        var normalized = MmsObjectReference.Parse(objectReference, functionalConstraint);
        if (!_session.IsMmsInitiated)
        {
            LastErrorMessage = _session.State == NativeIec61850AssociationState.MmsInitiateFailed
                ? $"Native TCP/COTP is connected, but ACSE/MMS Initiate was rejected or not understood by the IED. Planned object: {normalized}. Last response: {_session.LastAssociationResponseHex}"
                : $"Native transport is ready, but ACSE/MMS Initiate is not complete yet. Planned object: {normalized}. State: {_session.State}.";
            return null;
        }

        try
        {
            var result = await _session.ReadSingleVariableAsync(normalized, dataType, cancellationToken).ConfigureAwait(false);
            LastErrorMessage = result.IsSuccess
                ? result.Message
                : string.IsNullOrWhiteSpace(_session.LastReadAttemptSummary)
                    ? result.Message
                    : _session.LastReadAttemptSummary;
            return result.IsSuccess ? result.Value : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LastErrorMessage = $"Native MMS Confirmed-Read failed for {normalized}: {ex.GetType().Name}: {ex.Message}";
            return null;
        }
    }

    public ValueTask DisposeAsync() => _session.DisposeAsync();
}
