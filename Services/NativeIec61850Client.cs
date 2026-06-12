using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ari61850Bridge.Models;
using Ari61850Bridge.Protocol.Iec61850;
using Ari61850Bridge.Protocol.Mms;

namespace Ari61850Bridge.Services;

/// <summary>
/// Native IEC 61850 MMS client foundation.
///
/// Native IEC 61850 MMS client boundary. It starts as a native TCP/TPKT/COTP/ACSE/MMS
/// boundary and will be expanded phase-by-phase from SCL-driven reads to reporting.
/// </summary>
public sealed class NativeIec61850Client : IIec61850Client
{
    private readonly NativeIec61850Session _session = new();

    public bool IsConnected => _session.IsMmsInitiated;
    public bool IsTransportReady => _session.IsTransportConnected;
    public bool IsMmsReady => _session.IsMmsInitiated;
    public bool IsMmsInitiateFailed => _session.State == NativeIec61850AssociationState.MmsInitiateFailed;
    public string NativeState => _session.State.ToString();
    public string ConnectionMode => "Native IEC 61850 MMS";
    public string LastErrorMessage { get; private set; } = string.Empty;
    public string LastAssociationResponseHex => _session.LastAssociationResponseHex;
    public string LastAssociationAttemptSummary => _session.LastAssociationAttemptSummary;
    public string LastReadRequestHex => _session.LastReadRequestHex;
    public string LastReadResponseHex => _session.LastReadResponseHex;
    public string LastReadAttemptSummary => _session.LastReadAttemptSummary;
    public string LastDiscoveryRequestHex => _session.LastDiscoveryRequestHex;
    public string LastDiscoveryResponseHex => _session.LastDiscoveryResponseHex;
    public string LastDiscoverySummary { get; private set; } = string.Empty;
    public NativeReportInventory LastReportInventory { get; private set; } = new();

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
            LastErrorMessage = $"Native TCP/TPKT/COTP/ACSE preflight failed for {ipAddress}:{port}. {ex.GetType().Name}: {ex.Message}";
            await _session.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyList<SignalDefinition>> DiscoverSignalsAsync(CancellationToken cancellationToken)
    {
        LastDiscoverySummary = string.Empty;
        cancellationToken.ThrowIfCancellationRequested();

        if (!_session.IsMmsInitiated)
        {
            LastErrorMessage = $"Native online discovery requires ACSE/MMS association. Current state: {_session.State}. {_session.LastAssociationAttemptSummary}";
            return Array.Empty<SignalDefinition>();
        }

        try
        {
            var domainVariables = await _session.DiscoverDomainVariableNamesAsync(cancellationToken).ConfigureAwait(false);
            var domainVariableLists = await _session.DiscoverDomainVariableListNamesAsync(cancellationToken).ConfigureAwait(false);
            var snapshot = new NativeMmsDiscoverySnapshot
            {
                DomainVariables = domainVariables,
                DomainVariableLists = domainVariableLists
            };

            LastReportInventory = NativeReportDiscoveryMapper.BuildInventory(snapshot);

            var signals = NativeMmsDiscoveryMapper.BuildSignals(snapshot);

            LastDiscoverySummary = $"Native MMS GetNameList discovery: LD={domainVariables.Count}, raw variables={domainVariables.Values.Sum(v => v.Count)}, datasets={LastReportInventory.DataSets.Count}, RCB={LastReportInventory.ReportControls.Count} (BRCB={LastReportInventory.BufferedCount}, URCB={LastReportInventory.UnbufferedCount}), SCADA candidates={signals.Count}. Reporting inventory is read-only and not probed until the user opens the Edit IED Wizard Report Plan step.";
            LastErrorMessage = LastDiscoverySummary;
            return signals;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LastErrorMessage = $"Native MMS online discovery failed: {ex.GetType().Name}: {ex.Message}. Last discovery: {_session.LastDiscoveryAttemptSummary}. Last request: {_session.LastDiscoveryRequestHex}";
            return Array.Empty<SignalDefinition>();
        }
    }


    public async Task ProbeReportControlAsync(NativeReportControlCandidate rcb, CancellationToken cancellationToken)
    {
        if (rcb == null) throw new ArgumentNullException(nameof(rcb));
        cancellationToken.ThrowIfCancellationRequested();

        if (!_session.IsMmsInitiated)
        {
            rcb.Status = $"Probe blocked: ACSE/MMS not associated ({_session.State})";
            LastErrorMessage = rcb.Status;
            return;
        }

        rcb.Status = "Read-only attribute probe running";
        await TryReadReportAttributeAsync(rcb, "DatSet", value =>
        {
            var text = NormalizeReportAttributeText(value);
            if (!string.IsNullOrWhiteSpace(text))
                rcb.DataSetReference = NormalizeReportedDataSetReference(rcb.Domain, text);
        }, cancellationToken).ConfigureAwait(false);

        await TryReadReportAttributeAsync(rcb, "RptID", value => rcb.ReportId = NormalizeReportAttributeText(value), cancellationToken).ConfigureAwait(false);
        await TryReadReportAttributeAsync(rcb, "ConfRev", value => rcb.ConfRev = NormalizeReportAttributeText(value), cancellationToken).ConfigureAwait(false);
        await TryReadReportAttributeAsync(rcb, "IntgPd", value => rcb.IntegrityPeriodMs = NormalizeReportAttributeText(value), cancellationToken).ConfigureAwait(false);
        await TryReadReportAttributeAsync(rcb, "RptEna", value => rcb.EnabledState = NormalizeReportAttributeText(value), cancellationToken).ConfigureAwait(false);

        rcb.Status = string.IsNullOrWhiteSpace(rcb.DataSetReference)
            ? "Probed: DataSet not returned"
            : "Probed read-only";
        LastErrorMessage = rcb.Status;
    }

    private async Task TryReadReportAttributeAsync(NativeReportControlCandidate rcb, string attribute, Action<object?> apply, CancellationToken cancellationToken)
    {
        try
        {
            var value = await ReadValueAsync($"{rcb.Reference}.{attribute}", rcb.FunctionalConstraint, GuessReportAttributeType(attribute), cancellationToken).ConfigureAwait(false);
            if (value != null) apply(value);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            rcb.Status = $"Attribute probe partial: {attribute} {ex.GetType().Name}";
        }
    }

    private static string GuessReportAttributeType(string attribute)
    {
        return attribute.ToLowerInvariant() switch
        {
            "rptid" or "datset" or "entryid" => "String",
            "rptena" or "resv" or "gi" or "purgebuf" => "Boolean",
            "confrev" or "intgpd" or "buftm" or "sqnum" or "resvtms" => "UInt32",
            _ => "String"
        };
    }

    private static string NormalizeReportAttributeText(object? value)
    {
        var text = value?.ToString()?.Trim() ?? string.Empty;
        return string.IsNullOrWhiteSpace(text) ? string.Empty : text;
    }

    private static string NormalizeReportedDataSetReference(string domain, string value)
    {
        var text = value.Trim().Replace('$', '.');
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        if (text.Contains('/')) return text;

        // DatSet is commonly returned as LD/LN.DataSet or as a domain-local LN$DataSet.
        // If the server returns only a DataSet name, keep the LLN0 default as a planner hint.
        return text.Contains('.') ? $"{domain}/{text}" : $"{domain}/LLN0.{text}";
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
            LastErrorMessage = $"Native MMS Confirmed-Read failed for {normalized}: {ex.GetType().Name}: {ex.Message}. Last request: {_session.LastReadRequestHex}";
            return null;
        }
    }

    public ValueTask DisposeAsync() => _session.DisposeAsync();
}
