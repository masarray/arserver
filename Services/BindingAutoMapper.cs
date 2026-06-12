using Ari61850Bridge.Models;

namespace Ari61850Bridge.Services;

public static class BindingAutoMapper
{
    public static List<BindingItem> CreateBindings(IEnumerable<SignalDefinition> selectedSignals)
    {
        return CreateBindings(selectedSignals, relayBlockIndex: 0, blockSize: 1000);
    }

    public static List<BindingItem> CreateBindings(IEnumerable<SignalDefinition> selectedSignals, int relayBlockIndex, int blockSize = 1000)
    {
        var bindings = new List<BindingItem>();
        var normalizedIndex = Math.Max(0, relayBlockIndex);
        var normalizedBlock = Math.Max(200, blockSize);

        // Modbus reference notation is area-based. For multi-IED publishing we shift the
        // offset inside each area, not by moving a relay into another Modbus area.
        // IED-01: DI 10001+, IR 30001+, HR 40001+
        // IED-02: DI 11001+, IR 31001+, HR 41001+
        var discreteAddress = 10001 + normalizedIndex * normalizedBlock;
        var inputAddress = 30001 + normalizedIndex * normalizedBlock;
        var holdingAddress = 40001 + normalizedIndex * normalizedBlock;

        foreach (var signal in selectedSignals.OrderBy(GetModbusPriority).ThenBy(s => s.LogicalNode).ThenBy(s => s.Name))
        {
            var binding = new BindingItem
            {
                SignalName = signal.Name,
                IecReference = signal.ObjectReference,
                FunctionalConstraint = signal.FunctionalConstraint,
                IecDataType = signal.DataType,
                Category = signal.Category,
                Unit = signal.Unit,
                Quality = signal.Quality,
                DeviceTimestamp = signal.DeviceTimestamp,
                DataSetReference = signal.DataSetReference,
                ReportControlReference = signal.ReportControlReference,
                CurrentValue = signal.Value,
                LastUpdate = signal.Timestamp,
                Status = "Mapped",
                ReadMode = signal.IsReportCapable ? "RCB candidate / Polling fallback" : "MMS Polling",
                RcbMode = signal.IsReportCapable ? "SCL RCB candidate" : "None",
                FuxaTagName = MakeTagName(signal.Name)
            };

            if (signal.DataType is "Float32" or "Float")
            {
                binding.ModbusArea = "HoldingRegister";
                binding.ModbusAddress = holdingAddress;
                binding.ModbusDataType = "Float32";
                holdingAddress += 2;
            }
            else if (signal.DataType is "Boolean")
            {
                binding.ModbusArea = "DiscreteInput";
                binding.ModbusAddress = discreteAddress;
                binding.ModbusDataType = "Bool";
                discreteAddress += 1;
            }
            else
            {
                binding.ModbusArea = "InputRegister";
                binding.ModbusAddress = inputAddress;
                binding.ModbusDataType = "UInt16";
                inputAddress += 1;
            }

            bindings.Add(binding);
        }

        return bindings;
    }

    public static void ArrangeExistingBindings(IList<BindingItem> bindings, int relayBlockIndex, int blockSize = 1000)
    {
        if (bindings.Count == 0) return;

        var normalizedIndex = Math.Max(0, relayBlockIndex);
        var normalizedBlock = Math.Max(200, blockSize);
        var discreteAddress = 10001 + normalizedIndex * normalizedBlock;
        var inputAddress = 30001 + normalizedIndex * normalizedBlock;
        var holdingAddress = 40001 + normalizedIndex * normalizedBlock;

        foreach (var binding in bindings.OrderBy(GetBindingPriority).ThenBy(b => b.SignalName).ThenBy(b => b.IecReference))
        {
            if (string.Equals(binding.ModbusDataType, "Float32", StringComparison.OrdinalIgnoreCase))
            {
                binding.ModbusArea = "HoldingRegister";
                binding.ModbusAddress = holdingAddress;
                holdingAddress += 2;
                continue;
            }

            if (string.Equals(binding.ModbusDataType, "Bool", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(binding.IecDataType, "Boolean", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(binding.Category, "Protection", StringComparison.OrdinalIgnoreCase))
            {
                binding.ModbusArea = "DiscreteInput";
                binding.ModbusAddress = discreteAddress;
                discreteAddress += 1;
                continue;
            }

            binding.ModbusArea = "InputRegister";
            binding.ModbusAddress = inputAddress;
            inputAddress += 1;
        }
    }

    private static int GetBindingPriority(BindingItem binding)
    {
        if (string.Equals(binding.Category, "Position", StringComparison.OrdinalIgnoreCase)) return 10;
        if (string.Equals(binding.Category, "Protection", StringComparison.OrdinalIgnoreCase)) return 100;
        if (string.Equals(binding.Category, "Measurement", StringComparison.OrdinalIgnoreCase)) return 300;
        return 400;
    }

    private static int GetModbusPriority(SignalDefinition signal)
    {
        var cls = signal.LogicalNodeClass.ToUpperInvariant();
        var r = (signal.ObjectReference ?? string.Empty).ToLowerInvariant();

        // FUXA/SCADA map order: CB/DS position first, protection second, analog measurements last.
        if ((cls is "CSWI" or "XCBR" or "XSWI") || r.Contains(".pos.stval")) return 10;
        if (cls is "PTOC" or "PTRC" or "PDIF" or "PDIS" or "PIOC" or "PTOV" or "PTUV" or "PTEF" or "PDEF") return 100;
        if (cls is "MMXU" or "MMXN" || string.Equals(signal.Category, "Measurement", StringComparison.OrdinalIgnoreCase)) return 300;
        return 400;
    }

    private static string MakeTagName(string name)
    {
        var allowed = name.Where(ch => char.IsLetterOrDigit(ch) || ch == ' ').ToArray();
        return string.Join("_", new string(allowed).Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
    }
}
