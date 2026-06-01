using System.IO;
using System.Net.Sockets;
using System.Reflection;
using Ari61850Bridge.Models;

namespace Ari61850Bridge.Services;

/// <summary>
/// Real IEC 61850 MMS adapter using libiec61850.NET through runtime reflection.
///
/// Design rule for field use:
/// - Network/association failures are captured as status, not thrown to the UI.
/// - Online discovery is based on the real MMS model directory, not hard-coded mock points.
/// - Read operations still throw per-signal errors so runtime diagnostics can mark only the affected point.
/// </summary>
public sealed class RealLibIec61850Client : IIec61850Client
{
    private object? _connection;
    private Type? _connectionType;
    private Type? _functionalConstraintType;
    private Type? _acsiClassType;
    private readonly Dictionary<string, SignalDefinition> _signalIndex = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _visitedDiscoveryRefs = new(StringComparer.OrdinalIgnoreCase);

    public bool IsConnected { get; private set; }
    public string ConnectionMode => "Real libiec61850.NET MMS Client";
    public string LastErrorMessage { get; private set; } = "";
    public string LastDiscoverySummary { get; private set; } = "";

    public static bool IsRuntimeLibraryAvailable()
    {
        try
        {
            var baseDir = AppContext.BaseDirectory;
            return Directory.EnumerateFiles(baseDir, "*.dll", SearchOption.TopDirectoryOnly)
                .Any(f => Path.GetFileName(f).Contains("IEC61850", StringComparison.OrdinalIgnoreCase) ||
                          Path.GetFileName(f).Contains("libiec", StringComparison.OrdinalIgnoreCase) ||
                          Path.GetFileName(f).Contains("iec61850", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    public Task ConnectAsync(string ipAddress, int port, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            LastErrorMessage = "";
            IsConnected = false;
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                LoadLibIec61850Api();

                if (_connectionType == null)
                {
                    LastErrorMessage = "IEC61850.Client.IedConnection type was not found. Copy iec61850dotnet.dll and native iec61850.dll beside Ari61850Bridge.exe.";
                    return;
                }

                var tcpProbe = ProbeTcpPort(ipAddress, port, cancellationToken);
                if (!tcpProbe.Success)
                {
                    LastErrorMessage = $"TCP preflight failed for IEC 61850 MMS endpoint {ipAddress}:{port}. {tcpProbe.Message}. Check relay IP, subnet/VLAN, Windows firewall, test PC NIC, and whether TCP port 102 is open on the relay.";
                    return;
                }

                _connection = Activator.CreateInstance(_connectionType);
                if (_connection == null)
                {
                    LastErrorMessage = "Failed to create IEC61850.Client.IedConnection instance.";
                    return;
                }

                TrySetConnectTimeout(_connection, 5000);
                InvokeVoidFlexible(_connection, "Connect", ipAddress, port);
                IsConnected = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var root = UnwrapReflectionException(ex);
                LastErrorMessage = $"IEC 61850 MMS connect failed to {ipAddress}:{port}. {root.GetType().Name}: {root.Message}";
                SafeClose();
                IsConnected = false;
            }
        }, cancellationToken);
    }

    public Task<IReadOnlyList<SignalDefinition>> DiscoverSignalsAsync(CancellationToken cancellationToken)
    {
        return Task.Run<IReadOnlyList<SignalDefinition>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureConnected();
            LastDiscoverySummary = "";
            _visitedDiscoveryRefs.Clear();

            TryInvoke(_connection!, "UpdateDeviceModel");

            var logicalDevices = InvokeStringListFlexible(_connection!, "GetServerDirectory", false);
            if (logicalDevices.Count == 0)
                logicalDevices = InvokeStringListFlexible(_connection!, "GetServerDirectory");

            var result = new List<SignalDefinition>();
            var now = DateTime.Now;

            foreach (var ld in logicalDevices.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var logicalNodes = InvokeStringListFlexible(_connection!, "GetLogicalDeviceDirectory", ld);

                // Keep a visible breadcrumb when a relay exposes LD but directory browsing is restricted.
                if (logicalNodes.Count == 0)
                {
                    result.Add(CreateDirectorySignal($"Logical Device {ld}", ld, "IED", now));
                    continue;
                }

                foreach (var ln in logicalNodes.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var lnRef = BuildLogicalNodeReference(ld, ln);
                    var dataObjects = GetLogicalNodeDataObjects(lnRef);

                    if (dataObjects.Count == 0)
                    {
                        // Some devices/wrappers do not expose GetLogicalNodeDirectory in a friendly way.
                        // Try direct data-directory browsing as a fallback.
                        ExploreDataDirectory(result, lnRef, null, 0, now, cancellationToken);
                        continue;
                    }

                    foreach (var doEntry in dataObjects.Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        var parsed = ParseDirectoryEntry(doEntry, null);
                        if (string.IsNullOrWhiteSpace(parsed.Name)) continue;
                        var doRef = BuildChildReference(lnRef, parsed.Name);
                        ExploreDataDirectory(result, doRef, parsed.FunctionalConstraint, 0, now, cancellationToken);
                        if (result.Count >= 12000) break;
                    }

                    if (result.Count >= 12000) break;
                }

                if (result.Count >= 12000) break;
            }

            // If recursive browse produced only meta rows, add canonical probe points as a last resort.
            // These are marked Medium/Low and are not mock values; they are only candidate addresses for manual testing.
            if (result.Count == 0 && logicalDevices.Count > 0)
            {
                foreach (var ld in logicalDevices)
                    result.Add(CreateDirectorySignal($"{ld} online model detected", ld, "IED", now));
            }

            // Deduplicate and prefer actual value leaves over directory breadcrumbs.
            result = result
                .GroupBy(s => s.ObjectReference, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(x => x.Category != "Directory").ThenByDescending(x => ConfidenceScore(x.Confidence)).First())
                .OrderBy(s => s.SortPriority)
                .ThenByDescending(s => ConfidenceScore(s.Confidence))
                .ThenBy(s => s.LogicalNode, StringComparer.OrdinalIgnoreCase)
                .ThenBy(s => s.ObjectReference, StringComparer.OrdinalIgnoreCase)
                .ToList();

            _signalIndex.Clear();
            foreach (var signal in result)
                _signalIndex[signal.ObjectReference] = signal;

            LastDiscoverySummary = $"Online MMS discovery: LD={logicalDevices.Count}, signals={result.Count}. Smart sorted. No mock dataset used.";
            return result;
        }, cancellationToken);
    }

    public Task<object?> ReadValueAsync(string objectReference, CancellationToken cancellationToken)
    {
        var info = _signalIndex.TryGetValue(objectReference, out var signal) ? signal : null;
        var fcText = info?.FunctionalConstraint ?? InferFunctionalConstraint(objectReference);
        var dataType = info?.DataType ?? InferDataType(objectReference, fcText);
        return ReadValueAsync(objectReference, fcText, dataType, cancellationToken);
    }

    public Task<object?> ReadValueAsync(string objectReference, string functionalConstraint, string dataType, CancellationToken cancellationToken)
    {
        return Task.Run<object?>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureConnected();

            var fcText = string.IsNullOrWhiteSpace(functionalConstraint) ? InferFunctionalConstraint(objectReference) : functionalConstraint;
            var resolvedDataType = string.IsNullOrWhiteSpace(dataType) ? InferDataType(objectReference, fcText) : dataType;

            try
            {
                var value = ReadValueInternal(objectReference, fcText, resolvedDataType);
                if (IsLibIecErrorValue(value))
                {
                    LastErrorMessage = $"IEC 61850 read returned error for {objectReference} [{fcText}]: {value}";
                    return null;
                }
                return value;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var root = UnwrapReflectionException(ex);

                if (IsNonFatalReadError(root))
                {
                    LastErrorMessage = $"IEC 61850 read skipped for {objectReference} [{fcText}]: {root.GetType().Name} — {root.Message}";
                    return null;
                }

                LastErrorMessage = $"IEC 61850 read failed for {objectReference} [{fcText}]: {root.GetType().Name} — {root.Message}";
                SafeClose();
                IsConnected = false;
                throw new InvalidOperationException(LastErrorMessage, root);
            }
        }, cancellationToken);
    }

    private List<string> GetLogicalNodeDataObjects(string lnRef)
    {
        var acsiDataObject = CreateAcsiClass("ACSI_CLASS_DATA_OBJECT", "DATA_OBJECT");

        if (acsiDataObject != null)
        {
            try
            {
                var method = FindMethod(_connectionType!, "GetLogicalNodeDirectory", typeof(string), _acsiClassType!);
                if (method != null)
                {
                    var typed = ToStringList(method.Invoke(_connection, new[] { lnRef, acsiDataObject }));
                    if (typed.Count > 0) return typed;
                }
            }
            catch
            {
                // Fall through to wrapper variants below.
            }
        }

        // Some libiec61850 .NET wrapper builds expose GetLogicalNodeDirectory(string)
        // without the ACSIClass argument. Try it so MMXU/XCBR/PTOC nodes are not missed
        // just because the wrapper method signature differs.
        var generic = InvokeStringListFlexible(_connection!, "GetLogicalNodeDirectory", lnRef);
        if (generic.Count > 0) return generic;

        return new List<string>();
    }

    private void ExploreDataDirectory(List<SignalDefinition> result, string reference, string? inheritedFc, int depth, DateTime now, CancellationToken cancellationToken)
    {
        if (depth > 7 || result.Count >= 12000) return;
        if (!_visitedDiscoveryRefs.Add($"{reference}|{inheritedFc}|{depth}")) return;

        cancellationToken.ThrowIfCancellationRequested();

        var entries = InvokeStringListFlexible(_connection!, "GetDataDirectoryFC", reference);
        if (entries.Count == 0)
            entries = inheritedFc != null
                ? InvokeStringListWithFc(reference, inheritedFc)
                : InvokeStringListFlexible(_connection!, "GetDataDirectory", reference);

        if (entries.Count == 0)
        {
            var signal = CreateSignalFromReference(reference, inheritedFc ?? InferFunctionalConstraint(reference), now);
            if (ShouldExposeSignal(signal)) result.Add(signal);
            return;
        }

        foreach (var rawEntry in entries.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (result.Count >= 12000) return;
            var parsed = ParseDirectoryEntry(rawEntry, inheritedFc);
            if (string.IsNullOrWhiteSpace(parsed.Name)) continue;

            var childRef = BuildChildReference(reference, parsed.Name);
            var fc = parsed.FunctionalConstraint ?? inheritedFc;

            if (LooksLikeValueLeaf(childRef, fc))
            {
                var signal = CreateSignalFromReference(childRef, fc ?? InferFunctionalConstraint(childRef), now);
                if (ShouldExposeSignal(signal)) result.Add(signal);
                continue;
            }

            // Try deeper; if there are no child members, it will be added as a leaf by recursive fallback.
            ExploreDataDirectory(result, childRef, fc, depth + 1, now, cancellationToken);
        }
    }

    private List<string> InvokeStringListWithFc(string reference, string fcText)
    {
        try
        {
            var fc = CreateFunctionalConstraint(fcText);
            var method = FindMethod(_connectionType!, "GetDataDirectory", typeof(string), _functionalConstraintType!);
            if (method == null) return new List<string>();
            return ToStringList(method.Invoke(_connection, new[] { reference, fc }));
        }
        catch
        {
            return new List<string>();
        }
    }

    private SignalDefinition CreateSignalFromReference(string objectReference, string fc, DateTime now)
    {
        var dataType = InferDataType(objectReference, fc);
        var ln = ExtractLogicalNodeName(objectReference);
        var category = InferCategory(objectReference, ln);
        var unit = InferUnit(objectReference);
        var isCoreScada = SignalDefinition.IsCoreScadaSignal(objectReference, SignalDefinition.DetectLogicalNodeClass(ln), dataType, category);
        var confidence = InferConfidence(objectReference, dataType, category, isCoreScada);
        var name = MakeFriendlyName(objectReference, category);

        return new SignalDefinition
        {
            Name = name,
            ObjectReference = objectReference,
            FunctionalConstraint = fc,
            DataType = dataType,
            Category = category,
            Unit = unit,
            Confidence = confidence,
            IsReportCapable = isCoreScada && (fc is "ST" or "MX"),
            IsSelected = isCoreScada,
            Value = "Pending read",
            Quality = "Pending",
            Timestamp = now
        };
    }

    private static SignalDefinition CreateDirectorySignal(string name, string objectReference, string category, DateTime now)
    {
        return new SignalDefinition
        {
            Name = name,
            ObjectReference = objectReference,
            FunctionalConstraint = "-",
            DataType = "Directory",
            Category = category,
            Unit = "",
            Confidence = "Low",
            IsReportCapable = false,
            IsSelected = false,
            Value = "Online directory",
            Quality = "Unknown",
            Timestamp = now
        };
    }

    private static bool ShouldExposeSignal(SignalDefinition signal)
    {
        if (signal.DataType == "Directory") return true;

        var normalized = NormalizeReference(signal.ObjectReference);

        // Do not throw away the online model too early. The UI has a smart default filter;
        // discovery should remain complete enough for search/debug so a real connected IED
        // never looks disconnected just because our recommendation rule is too strict.
        if (signal.IsScadaCoreSignal) return true;

        // Keep obvious status signals searchable, but not auto-selected.
        if ((signal.FunctionalConstraint is "ST" or "MX") &&
            signal.DataType is "Boolean" or "Enum" or "Float32" or "Int32" or "UInt16")
        {
            // Hide noisy engineering leaves from normal discovery storage. These are not useful
            // as FUXA tags and create huge operator noise. Quality/timestamp can be added later
            // as companion diagnostics, not as primary SCADA tags.
            if (normalized.EndsWith(".q") || normalized.EndsWith(".t")) return false;
            if (normalized.Contains(".origin") || normalized.Contains(".ctlmodel") || normalized.Contains(".ctlval")) return false;
            if (normalized.Contains(".numpts") || normalized.Contains(".olddata") || normalized.Contains(".configrev")) return false;
            if (normalized.Contains(".mod.") || normalized.Contains(".beh.")) return false;
            return true;
        }

        return false;
    }

    private object? ReadValueInternal(string objectReference, string fcText, string dataType)
    {
        var fc = CreateFunctionalConstraint(fcText);

        // Hard field rule for libiec61850:
        // Do NOT probe multiple typed reads (ReadBooleanValue/ReadBitStringValue/ReadIntegerValue)
        // on an unknown vendor model. If the FC/object/type does not match perfectly, libiec61850
        // throws IedConnectionException/Data access error. That is normal IEC 61850 behavior, but it
        // is poisonous for a gateway runtime when repeated for hundreds of tags.
        //
        // Correct pattern for this MVP:
        // 1. Use the FC discovered from GetDataDirectoryFC.
        // 2. Use generic ReadValue(objectReference, fc) once.
        // 3. Decode the returned MmsValue locally.
        // 4. If the relay rejects the object, mark the tag Bad/Not readable and continue.
        var raw = TryReadMethod(new[] { "ReadValue" }, objectReference, fc);
        if (IsLibIecErrorValue(raw)) return null;

        var value = ExtractPrimitiveFromMmsValue(raw, dataType, objectReference);
        if (IsLibIecErrorValue(value)) return null;

        if (dataType.Equals("Float32", StringComparison.OrdinalIgnoreCase))
        {
            if (value is float or double or decimal) return Convert.ToDouble(value);
            if (double.TryParse(value?.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)) return d;
            return null;
        }

        if (dataType.Equals("Boolean", StringComparison.OrdinalIgnoreCase))
        {
            if (value is bool b) return b;
            if (TryNormalizeCodedValue(value, out var normalized))
            {
                if (normalized is bool nb) return nb;
                if (normalized is byte or sbyte or short or ushort or int or uint or long or ulong)
                    return Convert.ToInt32(normalized) != 0;

                var text = normalized?.ToString()?.Trim();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    if (bool.TryParse(text, out var parsedBool)) return parsedBool;
                    if (int.TryParse(text, out var parsedInt)) return parsedInt != 0;
                }
            }
            return null;
        }

        if (dataType.Equals("Dbpos", StringComparison.OrdinalIgnoreCase) ||
            dataType.Equals("Enum", StringComparison.OrdinalIgnoreCase) ||
            dataType.Equals("Int32", StringComparison.OrdinalIgnoreCase) ||
            dataType.Equals("UInt16", StringComparison.OrdinalIgnoreCase) ||
            dataType.Equals("Integer", StringComparison.OrdinalIgnoreCase))
        {
            if (IsDoublePointStatusReference(objectReference))
            {
                var dbpos = NormalizeDbposValue(value);
                if (dbpos.HasValue) return dbpos.Value;
            }

            if (TryNormalizeCodedValue(value, out var normalized)) return normalized;
            return null;
        }

        if (dataType.Equals("Quality", StringComparison.OrdinalIgnoreCase) ||
            dataType.Equals("Timestamp", StringComparison.OrdinalIgnoreCase))
            return value?.ToString();

        return value?.ToString();
    }

    private static object? ExtractPrimitiveFromMmsValue(object? raw, string dataType, string? objectReference = null)
    {
        if (raw == null) return null;
        if (raw is bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal or string)
            return raw;

        var type = raw.GetType();
        var fullName = type.FullName ?? type.Name;
        if (!fullName.Contains("MmsValue", StringComparison.OrdinalIgnoreCase))
            return raw;

        var mmsType = GetMmsValueTypeName(raw);

        object? TryNoArg(string methodName)
        {
            try
            {
                var method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes);
                return method?.Invoke(raw, Array.Empty<object>());
            }
            catch
            {
                return null;
            }
        }

        bool IsMms(params string[] tokens)
        {
            if (string.IsNullOrWhiteSpace(mmsType)) return false;
            return tokens.Any(t => mmsType.Contains(t, StringComparison.OrdinalIgnoreCase));
        }

        // Important libiec61850 rule:
        // MmsValue conversion methods throw MmsValueException when the native MMS type does not match.
        // Do not call ToInt32/ToFloat/GetBoolean/BitStringToUInt32 blindly; inspect MmsValue.GetType() first.
        // This avoids debugger-breaking first-chance exceptions and keeps runtime clean.
        if (dataType.Equals("Float32", StringComparison.OrdinalIgnoreCase))
        {
            if (IsMms("FLOAT"))
                return TryNoArg("ToFloat") ?? TryNoArg("ToDouble") ?? raw.ToString();
            return raw.ToString();
        }

        if (dataType.Equals("Boolean", StringComparison.OrdinalIgnoreCase))
        {
            if (IsMms("BOOLEAN"))
                return TryNoArg("GetBoolean") ?? raw.ToString();
            if (IsMms("INTEGER", "UNSIGNED"))
                return TryNoArg("ToInt32") ?? TryNoArg("ToUint32") ?? raw.ToString();
            if (IsMms("BIT_STRING", "BITSTRING"))
                return TryNoArg("BitStringToUInt32") ?? TryNoArg("BitStringToUInt32BigEndian") ?? raw.ToString();
            return raw.ToString();
        }

        if (dataType.Equals("Dbpos", StringComparison.OrdinalIgnoreCase) ||
            dataType.Equals("Enum", StringComparison.OrdinalIgnoreCase) ||
            dataType.Equals("Int32", StringComparison.OrdinalIgnoreCase) ||
            dataType.Equals("UInt16", StringComparison.OrdinalIgnoreCase) ||
            dataType.Equals("Integer", StringComparison.OrdinalIgnoreCase))
        {
            if (IsMms("INTEGER", "UNSIGNED", "ENUMERATED"))
                return TryNoArg("ToInt32") ?? TryNoArg("ToUint32") ?? raw.ToString();
            if (IsMms("BOOLEAN"))
                return TryNoArg("GetBoolean") ?? raw.ToString();
            if (IsMms("BIT_STRING", "BITSTRING"))
            {
                if (IsDoublePointStatusReference(objectReference) || dataType.Equals("Dbpos", StringComparison.OrdinalIgnoreCase))
                    return DecodeIec61850DbposBitString(raw, TryNoArg) ?? raw.ToString();

                return TryNoArg("BitStringToUInt32") ?? TryNoArg("BitStringToUInt32BigEndian") ?? raw.ToString();
            }
            return raw.ToString();
        }

        if (dataType.Equals("Timestamp", StringComparison.OrdinalIgnoreCase))
        {
            if (IsMms("UTC_TIME", "BINARY_TIME"))
                return TryNoArg("GetUtcTimeAsDateTimeOffset") ?? TryNoArg("GetUtcTimeInMs") ?? TryNoArg("GetBinaryTimeAsUtcMs") ?? raw.ToString();
            return raw.ToString();
        }

        if (dataType.Equals("Quality", StringComparison.OrdinalIgnoreCase))
        {
            if (IsMms("BIT_STRING", "BITSTRING"))
                return TryNoArg("BitStringToUInt32") ?? TryNoArg("BitStringToUInt32BigEndian") ?? raw.ToString();
            return raw.ToString();
        }

        return raw.ToString();
    }

    private static bool IsDoublePointStatusReference(string? objectReference)
    {
        if (string.IsNullOrWhiteSpace(objectReference)) return false;
        var r = objectReference.Replace('$', '.').ToLowerInvariant();
        return r.EndsWith(".pos.stval", StringComparison.OrdinalIgnoreCase) ||
               r.Contains(".pos.stval", StringComparison.OrdinalIgnoreCase);
    }

    private static object? DecodeIec61850DbposBitString(object raw, Func<string, object?> tryNoArg)
    {
        // IEC 61850 DPC/Dbpos stVal is a two-bit state:
        // 0 = intermediate-state, 1 = off/open, 2 = on/closed, 3 = bad-state.
        // libiec61850 has two bit-string numeric helpers. For Dbpos, the big-endian
        // interpretation matches the standard bit pattern 01=open/off and 10=closed/on.
        // Calling BitStringToUInt32 first reverses many real IED positions.
        var be = tryNoArg("BitStringToUInt32BigEndian");
        if (TryNormalizeCodedValue(be, out var normalizedBe) && IsDbposCode(normalizedBe))
            return Convert.ToInt32(normalizedBe, System.Globalization.CultureInfo.InvariantCulture);

        var le = tryNoArg("BitStringToUInt32");
        if (TryNormalizeCodedValue(le, out var normalizedLe) && IsDbposCode(normalizedLe))
            return Convert.ToInt32(normalizedLe, System.Globalization.CultureInfo.InvariantCulture);

        var text = raw.ToString();
        if (string.IsNullOrWhiteSpace(text)) return null;
        var compact = text.Replace(" ", "").Replace("_", "").ToLowerInvariant();
        if (compact.Contains("01")) return 1;
        if (compact.Contains("10")) return 2;
        if (compact.Contains("00")) return 0;
        if (compact.Contains("11")) return 3;
        return null;
    }


    private static int? NormalizeDbposValue(object? value)
    {
        if (value == null) return null;

        if (TryNormalizeCodedValue(value, out var normalized))
        {
            if (IsDbposCode(normalized))
                return Convert.ToInt32(normalized, System.Globalization.CultureInfo.InvariantCulture);
        }

        var text = value.ToString()?.Trim();
        if (string.IsNullOrWhiteSpace(text)) return null;

        var compact = text.Replace(" ", "").Replace("_", "").Replace("-", "").ToLowerInvariant();
        return compact switch
        {
            "00" => 0,
            "01" => 1,
            "10" => 2,
            "11" => 3,
            "open" or "off" => 1,
            "closed" or "close" or "on" => 2,
            "intermediate" or "intermediatestate" => 0,
            "bad" or "badstate" => 3,
            _ => null
        };
    }

    private static bool IsDbposCode(object? value)
    {
        try
        {
            var i = Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
            return i is >= 0 and <= 3;
        }
        catch
        {
            return false;
        }
    }

    private static string GetMmsValueTypeName(object raw)
    {
        try
        {
            var type = raw.GetType();
            // IEC61850.Common.MmsValue hides object.GetType() with "new MmsType GetType()".
            // Reflection can otherwise pick System.Object.GetType(), so explicitly avoid it.
            var method = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m =>
                    m.Name == "GetType" &&
                    m.DeclaringType != typeof(object) &&
                    m.GetParameters().Length == 0);

            var mmsType = method?.Invoke(raw, Array.Empty<object>());
            return mmsType?.ToString() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool IsLibIecErrorValue(object? value)
    {
        var text = value?.ToString();
        if (string.IsNullOrWhiteSpace(text)) return false;
        return text.StartsWith("error:", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("OBJECT_NONE_EXISTENT", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("OBJECT_NON_EXISTENT", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("DATA_ACCESS", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Data access error", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("not readable", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("ACCESS", StringComparison.OrdinalIgnoreCase) && text.Contains("ERROR", StringComparison.OrdinalIgnoreCase);
    }


    private static bool TryNormalizeCodedValue(object? value, out object? normalized)
    {
        normalized = null;
        if (value == null) return false;
        if (IsLibIecErrorValue(value)) return false;

        if (value is bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal)
        {
            normalized = value;
            return true;
        }

        var type = value.GetType();
        if (type.IsEnum)
        {
            normalized = Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
            return true;
        }

        var text = value.ToString()?.Trim();
        if (string.IsNullOrWhiteSpace(text) || IsLibIecErrorValue(text)) return false;

        // Common libiec61850/MmsValue textual forms can be verbose. Extract the most useful primitive.
        if (bool.TryParse(text, out var b))
        {
            normalized = b;
            return true;
        }

        if (int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var i))
        {
            normalized = i;
            return true;
        }

        if (uint.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var ui))
        {
            normalized = ui;
            return true;
        }

        if (double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d))
        {
            normalized = d;
            return true;
        }

        // Try last numeric token in strings such as "MmsValue(2)" or "bit-string: 01".
        var matches = System.Text.RegularExpressions.Regex.Matches(text, @"[-+]?\d+(?:\.\d+)?");
        if (matches.Count > 0)
        {
            var token = matches[^1].Value;
            if (int.TryParse(token, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var tokenInt))
            {
                normalized = tokenInt;
                return true;
            }
            if (double.TryParse(token, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var tokenDouble))
            {
                normalized = tokenDouble;
                return true;
            }
        }

        normalized = text;
        return true;
    }

    private static bool IsNonFatalReadError(Exception root)
    {
        var message = root.Message ?? string.Empty;
        if (IsDisconnectedReadError(root)) return false;

        return root is FormatException ||
               root.GetType().Name.Contains("MmsValueException", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("OBJECT_NONE_EXISTENT", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("OBJECT_NON_EXISTENT", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Data access error", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("not of type", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("type inconsistent", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("access denied", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("service not supported", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDisconnectedReadError(Exception root)
    {
        var message = root.Message ?? string.Empty;
        return message.Contains("not connected", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("disconnected", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("connection closed", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("connection reset", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("connection lost", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("association", StringComparison.OrdinalIgnoreCase) && message.Contains("closed", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("socket", StringComparison.OrdinalIgnoreCase) && message.Contains("closed", StringComparison.OrdinalIgnoreCase);
    }

    private object? ReadCodedStatusValue(string objectReference, object fc)
    {
        var raw = TryReadMethod(new[] { "ReadValue" }, objectReference, fc);
        var value = ExtractPrimitiveFromMmsValue(raw, "Enum");
        if (TryNormalizeCodedValue(value, out var normalized)) return normalized;
        return null;
    }

    private object? ReadIntegerLikeValue(string objectReference, object fc)
    {
        var raw = TryReadMethod(new[] { "ReadValue" }, objectReference, fc);
        var value = ExtractPrimitiveFromMmsValue(raw, "Int32");
        if (TryNormalizeCodedValue(value, out var normalized)) return normalized;
        return null;
    }

    private object? TryReadMethod(string[] methodNames, string objectReference, object fc)
    {
        foreach (var name in methodNames)
        {
            try
            {
                var method = FindMethod(_connectionType!, name, typeof(string), _functionalConstraintType!);
                if (method == null) continue;
                var value = method.Invoke(_connection, new[] { objectReference, fc });
                if (IsLibIecErrorValue(value)) continue;
                return value;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var root = UnwrapReflectionException(ex);
                // Data-access errors while reading individual tags are normal in multi-vendor IEC 61850.
                // Keep them local to the tag; runtime will mark the signal Bad/Not readable.
                if (IsDisconnectedReadError(root))
                {
                    SafeClose();
                    IsConnected = false;
                    throw new InvalidOperationException($"IEC61850 client disconnected while reading {objectReference}: {root.Message}", root);
                }
                if (IsNonFatalReadError(root)) continue;
                throw;
            }
        }
        return null;
    }

    private object? InvokeReadMethod(string[] methodNames, string objectReference, object fc)
    {
        foreach (var name in methodNames)
        {
            var method = FindMethod(_connectionType!, name, typeof(string), _functionalConstraintType!);
            if (method == null) continue;
            return method.Invoke(_connection, new[] { objectReference, fc });
        }

        var readValue = FindMethod(_connectionType!, "ReadValue", typeof(string), _functionalConstraintType!);
        if (readValue != null)
            return readValue.Invoke(_connection, new[] { objectReference, fc });

        throw new MissingMethodException(_connectionType!.FullName, string.Join("/", methodNames));
    }

    private object CreateFunctionalConstraint(string fcText)
    {
        if (_functionalConstraintType == null)
            throw new InvalidOperationException("FunctionalConstraint enum was not found in the loaded IEC61850 wrapper.");

        var normalized = NormalizeFc(fcText);
        if (Enum.TryParse(_functionalConstraintType, normalized, ignoreCase: true, out var fc))
            return fc!;

        var names = Enum.GetNames(_functionalConstraintType);
        var matched = names.FirstOrDefault(n =>
            n.Equals($"FC_{normalized}", StringComparison.OrdinalIgnoreCase) ||
            n.Equals(normalized, StringComparison.OrdinalIgnoreCase) ||
            n.EndsWith($"_{normalized}", StringComparison.OrdinalIgnoreCase));

        if (matched != null)
            return Enum.Parse(_functionalConstraintType, matched);

        throw new InvalidOperationException($"Functional constraint '{fcText}' is not supported by loaded IEC61850 wrapper.");
    }

    private object? CreateAcsiClass(params string[] names)
    {
        if (_acsiClassType == null) return null;
        var enumNames = Enum.GetNames(_acsiClassType);
        foreach (var name in names)
        {
            var matched = enumNames.FirstOrDefault(n => n.Equals(name, StringComparison.OrdinalIgnoreCase) || n.EndsWith(name, StringComparison.OrdinalIgnoreCase));
            if (matched != null) return Enum.Parse(_acsiClassType, matched);
        }
        return null;
    }

    private static bool IsFunctionalConstraintToken(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var token = text.Trim().Trim('[', ']', '(', ')').ToUpperInvariant();
        return token is "ST" or "MX" or "CO" or "CF" or "DC" or "SP" or "SG" or "SE" or "SV" or "EX" or "BR" or "RP" or "LG" or "GO" or "MS" or "US";
    }

    private static string NormalizeFc(string? fcText)
    {
        if (string.IsNullOrWhiteSpace(fcText)) return "ST";
        var fc = fcText.Trim().Trim('[', ']', '(', ')').ToUpperInvariant();
        if (fc.StartsWith("FC_")) fc = fc[3..];
        return fc switch
        {
            "ME" => "MX",
            "STATUS" => "ST",
            "MEASUREMENT" => "MX",
            _ => fc
        };
    }

    private static string InferFunctionalConstraint(string objectReference)
    {
        if (objectReference.Contains(".mag.", StringComparison.OrdinalIgnoreCase) ||
            objectReference.EndsWith(".f", StringComparison.OrdinalIgnoreCase) ||
            objectReference.EndsWith(".i", StringComparison.OrdinalIgnoreCase) ||
            objectReference.Contains(".cVal", StringComparison.OrdinalIgnoreCase)) return "MX";
        if (objectReference.Contains("ctl", StringComparison.OrdinalIgnoreCase)) return "CO";
        if (objectReference.Contains(".set", StringComparison.OrdinalIgnoreCase)) return "SP";
        return "ST";
    }

    private static string InferDataType(string objectReference, string? fcText)
    {
        var refLower = objectReference.ToLowerInvariant();
        var leaf = refLower.Split('.').LastOrDefault() ?? refLower;

        if (leaf == "q") return "Quality";
        if (leaf == "t") return "Timestamp";
        if (leaf == "f") return "Float32";
        if (leaf is "i" or "ctlmodel" or "stnum" or "sqnum" or "intaddr") return "Int32";
        if (leaf is "general" or "oper" or "blocked" or "test" or "on" or "off") return "Boolean";
        if (leaf == "stval") return "Enum";
        if (NormalizeFc(fcText) == "MX" && (refLower.Contains(".mag") || refLower.Contains(".cval"))) return "Float32";
        if (NormalizeFc(fcText) == "ST") return "Enum";
        return "String";
    }

    private static string InferCategory(string reference, string ln)
    {
        var lnClass = SignalDefinition.DetectLogicalNodeClass(ln);
        if (reference.EndsWith(".q", StringComparison.OrdinalIgnoreCase)) return "Quality";
        if (reference.EndsWith(".t", StringComparison.OrdinalIgnoreCase)) return "Timestamp";
        if (lnClass is "MMXU" or "MMXN" or "MSQI") return "Measurement";
        if (lnClass is "CSWI" or "XCBR" or "XSWI") return "Position";
        if (lnClass is "PTRC" or "PTOC" or "PDIF" or "PDIS" or "PIOC" or "PTOV" or "PTUV" or "PTEF" or "PDEF" or "PSCH" or "RREC" or "RBRF") return "Protection";
        if (lnClass is "GGIO" or "GAPC") return "GGIO";
        if (lnClass is "LLN0" or "LPHD") return "Health";
        return "Signal";
    }

    private static string InferConfidence(string reference, string dataType, string category, bool isCoreScada)
    {
        if (isCoreScada) return "High";
        if (category == "GGIO" && reference.EndsWith(".stVal", StringComparison.OrdinalIgnoreCase)) return "Medium";
        if (dataType is "Float32" or "Boolean" or "Enum") return "Medium";
        return "Low";
    }

    private static int ConfidenceScore(string confidence) => confidence switch
    {
        "High" => 3,
        "Medium" => 2,
        _ => 1
    };

    private static string InferUnit(string reference)
    {
        var r = NormalizeReference(reference);
        if (r.Contains(".a.phs") || r.Contains(".a.neut") || r.Contains(".a.net")) return "A";
        if (r.Contains(".phv.") || r.Contains(".ppv.")) return "V";
        if (r.Contains(".hz.")) return "Hz";
        if (r.Contains("totw") || r.Contains(".w.")) return "W";
        if (r.Contains("totvar") || r.Contains("var")) return "var";
        return "";
    }

    private static string MakeFriendlyName(string reference, string category)
    {
        var ln = ExtractLogicalNodeName(reference);
        var lnClass = SignalDefinition.DetectLogicalNodeClass(ln);
        var r = NormalizeReference(reference);

        if (lnClass is "CSWI" or "XCBR" or "XSWI")
        {
            if (r.EndsWith(".pos.stval")) return $"{ln} Position";
        }

        if (lnClass is "MMXU" or "MMXN")
        {
            if (r.Contains(".a.phsa.")) return $"{ln} Phase A Current";
            if (r.Contains(".a.phsb.")) return $"{ln} Phase B Current";
            if (r.Contains(".a.phsc.")) return $"{ln} Phase C Current";
            if (r.Contains(".a.neut.") || r.Contains(".a.net.")) return $"{ln} Neutral Current";
            if (r.Contains(".phv.phsa.")) return $"{ln} Phase A Voltage";
            if (r.Contains(".phv.phsb.")) return $"{ln} Phase B Voltage";
            if (r.Contains(".phv.phsc.")) return $"{ln} Phase C Voltage";
            if (r.Contains(".ppv.phsab.")) return $"{ln} AB Voltage";
            if (r.Contains(".ppv.phsbc.")) return $"{ln} BC Voltage";
            if (r.Contains(".ppv.phsca.")) return $"{ln} CA Voltage";
        }

        if (lnClass == "PTOC")
        {
            if (r.EndsWith(".op.general")) return $"{ln} Overcurrent Operate";
            if (r.EndsWith(".str.general")) return $"{ln} Overcurrent Start";
        }

        if (lnClass == "PTRC" && r.EndsWith(".tr.general")) return $"{ln} Trip General";
        if ((lnClass is "PDIF" or "PDIS" or "PIOC" or "PTOV" or "PTUV" or "PTEF" or "PDEF") && r.EndsWith(".op.general")) return $"{ln} Operate General";

        var leafPath = reference.Contains('.') ? reference[(reference.IndexOf('.') + 1)..] : reference;
        leafPath = leafPath.Replace("cVal.mag.f", "Value", StringComparison.OrdinalIgnoreCase)
                           .Replace("mag.f", "Value", StringComparison.OrdinalIgnoreCase)
                           .Replace("stVal", "Status", StringComparison.OrdinalIgnoreCase)
                           .Replace("general", "General", StringComparison.OrdinalIgnoreCase);
        return $"{ln} {leafPath}".Replace('.', ' ').Replace("  ", " ").Trim();
    }

    private static string NormalizeReference(string reference)
    {
        return (reference ?? string.Empty)
            .Replace('$', '.')
            .Replace("..", ".")
            .ToLowerInvariant();
    }

    private static string ExtractLogicalNodeName(string reference)
    {
        var slash = reference.IndexOf('/');
        if (slash < 0) return "IED";
        var afterSlash = reference[(slash + 1)..];
        var dot = afterSlash.IndexOf('.');
        return dot > 0 ? afterSlash[..dot] : afterSlash;
    }

    private static bool LooksLikeValueLeaf(string reference, string? fc)
    {
        var leaf = reference.Split('.').LastOrDefault()?.ToLowerInvariant() ?? reference.ToLowerInvariant();
        if (leaf is "stval" or "q" or "t" or "f" or "i" or "general" or "ctlmodel") return true;
        if (NormalizeFc(fc) is "ST" or "MX" && leaf is "origin" or "numpts" or "olddata") return true;
        return false;
    }

    private static string BuildLogicalNodeReference(string ld, string ln)
    {
        if (ln.Contains('/')) return ln.Trim();
        return $"{ld.TrimEnd('/')}/{ln.TrimStart('/')}";
    }

    private static string BuildChildReference(string parent, string child)
    {
        var clean = child.Trim();
        if (string.IsNullOrWhiteSpace(clean)) return parent;
        if (clean.Contains('/')) return clean;
        clean = clean.TrimStart('.', '$');
        return parent.EndsWith('.') ? parent + clean : parent + "." + clean;
    }

    private static (string Name, string? FunctionalConstraint) ParseDirectoryEntry(string raw, string? fallbackFc)
    {
        var text = (raw ?? string.Empty).Trim();
        if (text.Length == 0) return ("", fallbackFc);

        string? fc = fallbackFc;

        var bracketStart = text.LastIndexOf('[');
        var bracketEnd = text.LastIndexOf(']');
        if (bracketStart >= 0 && bracketEnd > bracketStart)
        {
            fc = text.Substring(bracketStart + 1, bracketEnd - bracketStart - 1);
            text = text[..bracketStart].Trim();
        }

        var parenStart = text.LastIndexOf('(');
        var parenEnd = text.LastIndexOf(')');
        if (parenStart >= 0 && parenEnd > parenStart && parenEnd == text.Length - 1)
        {
            var candidate = text.Substring(parenStart + 1, parenEnd - parenStart - 1).Trim();
            if (candidate.Length <= 4)
            {
                fc = candidate;
                text = text[..parenStart].Trim();
            }
        }

        // Some wrappers return MMS-style references with '$' separators.
        // Treat the last token as FC only when it is a real functional-constraint token
        // such as ST/MX/CO/CF. Do not misread data leaves like mag$f as FC=f.
        var dollar = text.LastIndexOf('$');
        if (dollar > 0 && dollar < text.Length - 1)
        {
            var candidate = text[(dollar + 1)..].Trim();
            if (IsFunctionalConstraintToken(candidate))
            {
                fc = candidate;
                text = text[..dollar].Trim();
            }
        }

        if (text.Contains('$'))
            text = text.Replace('$', '.');

        if (text.Contains(' '))
        {
            var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[^1].Length <= 4 && parts[^1].All(char.IsLetter))
            {
                fc = parts[^1];
                text = string.Join(" ", parts.Take(parts.Length - 1));
            }
        }

        return (text.Trim(), fc != null ? NormalizeFc(fc) : null);
    }

    private static (bool Success, string Message) ProbeTcpPort(string ipAddress, int port, CancellationToken cancellationToken)
    {
        try
        {
            ipAddress = (ipAddress ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(ipAddress))
                return (false, "Relay IP/hostname is empty. The connect request was blocked before TCP open.");

            using var client = new TcpClient();
            var connectTask = client.ConnectAsync(ipAddress, port);
            var completed = connectTask.Wait(TimeSpan.FromMilliseconds(2500));
            cancellationToken.ThrowIfCancellationRequested();

            if (!completed)
                return (false, $"TimeoutException: TCP {ipAddress}:{port} did not respond within 2500 ms");

            if (connectTask.IsFaulted)
            {
                var root = connectTask.Exception?.GetBaseException();
                return (false, root == null ? "Socket connection failed" : $"{root.GetType().Name}: {root.Message}");
            }

            return (true, "TCP reachable");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var root = UnwrapReflectionException(ex);
            return (false, $"{root.GetType().Name}: {root.Message}");
        }
    }

    private static Exception UnwrapReflectionException(Exception exception)
    {
        return exception switch
        {
            TargetInvocationException { InnerException: not null } tie => UnwrapReflectionException(tie.InnerException!),
            AggregateException { InnerException: not null } ae => UnwrapReflectionException(ae.InnerException!),
            InvalidOperationException { InnerException: not null } ioe when ioe.Message.StartsWith("Exception has been thrown", StringComparison.OrdinalIgnoreCase) => UnwrapReflectionException(ioe.InnerException!),
            _ => exception
        };
    }

    private void LoadLibIec61850Api()
    {
        if (_connectionType != null) return;

        var baseDir = AppContext.BaseDirectory;
        var candidates = Directory.EnumerateFiles(baseDir, "*.dll", SearchOption.TopDirectoryOnly)
            .Where(f => Path.GetFileName(f).Contains("IEC61850", StringComparison.OrdinalIgnoreCase) ||
                        Path.GetFileName(f).Contains("iec61850", StringComparison.OrdinalIgnoreCase) ||
                        Path.GetFileName(f).Contains("libiec", StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var dll in candidates)
        {
            try { Assembly.LoadFrom(dll); } catch { }
        }

        var allTypes = AppDomain.CurrentDomain.GetAssemblies().SelectMany(SafeGetTypes).ToList();
        _connectionType = allTypes.FirstOrDefault(t =>
            t.FullName == "IEC61850.Client.IedConnection" ||
            t.FullName == "IEC61850.Client.IedConnectionWrapper" ||
            t.Name == "IedConnection");

        _functionalConstraintType = allTypes.FirstOrDefault(t => t.Name == "FunctionalConstraint" && t.IsEnum);
        _acsiClassType = allTypes.FirstOrDefault(t => t.Name == "ACSIClass" && t.IsEnum);

        if (_connectionType == null)
        {
            var searched = candidates.Count == 0
                ? "No IEC61850/libiec*.dll files were found beside the EXE."
                : string.Join(Environment.NewLine, candidates.Select(Path.GetFileName));
            throw new FileNotFoundException("Real IEC61850 engine selected, but libiec61850.NET wrapper was not found. Searched:\n" + searched);
        }

        if (_functionalConstraintType == null)
            throw new FileNotFoundException("Real IEC61850 engine selected, but FunctionalConstraint enum was not found in the loaded IEC61850 wrapper DLL.");
    }

    private static MethodInfo? FindMethod(Type type, string methodName, params Type[] parameterTypes)
    {
        return type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == methodName && ParametersMatch(m, parameterTypes));
    }

    private static bool ParametersMatch(MethodInfo method, Type[] parameterTypes)
    {
        var parameters = method.GetParameters();
        if (parameters.Length != parameterTypes.Length) return false;
        for (var i = 0; i < parameters.Length; i++)
        {
            if (!parameters[i].ParameterType.IsAssignableFrom(parameterTypes[i]) && parameters[i].ParameterType != parameterTypes[i])
                return false;
        }
        return true;
    }

    private static void InvokeVoidFlexible(object target, string methodName, params object[] args)
    {
        var method = FindByNameAndArgumentCount(target.GetType(), methodName, args.Length)
            ?? throw new MissingMethodException(target.GetType().FullName, methodName);
        try
        {
            method.Invoke(target, args);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw UnwrapReflectionException(ex);
        }
    }

    private static List<string> InvokeStringListFlexible(object target, string methodName, params object[] args)
    {
        try
        {
            var method = FindByNameAndArgumentCount(target.GetType(), methodName, args.Length);
            if (method == null) return new List<string>();
            return ToStringList(method.Invoke(target, args));
        }
        catch
        {
            return new List<string>();
        }
    }

    private static MethodInfo? FindByNameAndArgumentCount(Type type, string methodName, int argCount)
    {
        return type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == methodName && m.GetParameters().Length == argCount);
    }

    private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null)!; }
        catch { return Array.Empty<Type>(); }
    }

    private static List<string> ToStringList(object? value)
    {
        if (value is IEnumerable<string> strings) return strings.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        if (value is System.Collections.IEnumerable enumerable)
        {
            var list = new List<string>();
            foreach (var item in enumerable)
            {
                var text = item?.ToString();
                if (!string.IsNullOrWhiteSpace(text)) list.Add(text);
            }
            return list;
        }
        return new List<string>();
    }

    private static void TryInvoke(object target, string methodName)
    {
        try { target.GetType().GetMethod(methodName, Type.EmptyTypes)?.Invoke(target, Array.Empty<object>()); }
        catch { }
    }

    private static void TrySetConnectTimeout(object target, uint timeoutMs)
    {
        try
        {
            var prop = target.GetType().GetProperty("ConnectTimeout", BindingFlags.Public | BindingFlags.Instance);
            if (prop != null && prop.CanWrite)
                prop.SetValue(target, timeoutMs);
        }
        catch { }
    }

    private void EnsureConnected()
    {
        if (!IsConnected || _connection == null || _connectionType == null)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(LastErrorMessage) ? "IEC61850 real client is not connected." : LastErrorMessage);
    }

    private void SafeClose()
    {
        try { _connectionType?.GetMethod("Close")?.Invoke(_connection, Array.Empty<object>()); } catch { }
        try { (_connection as IDisposable)?.Dispose(); } catch { }
        _connection = null;
    }

    public ValueTask DisposeAsync()
    {
        SafeClose();
        IsConnected = false;
        return ValueTask.CompletedTask;
    }
}
