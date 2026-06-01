using System.IO;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Xml.Linq;
using Ari61850Bridge.Models;

namespace Ari61850Bridge.Services;

public static class SclImportService
{
    public static SclImportResult Import(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("SCL file path is empty.", nameof(filePath));
        if (!File.Exists(filePath)) throw new FileNotFoundException("SCL file not found.", filePath);

        var doc = XDocument.Load(filePath, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
        var root = doc.Root ?? throw new InvalidOperationException("Invalid SCL file. Root element is missing.");
        XNamespace ns = root.Name.Namespace;

        var ieds = root.Elements(ns + "IED").ToList();
        if (ieds.Count == 0) throw new InvalidOperationException("No IED element found in SCL/CID/SCD file.");

        var communication = root.Element(ns + "Communication");
        var connectedAps = communication?
            .Descendants(ns + "ConnectedAP")
            .ToList() ?? new List<XElement>();

        var selectedIed = PickIed(ieds, connectedAps, ns);
        var iedName = Attr(selectedIed, "name", "IED");
        var connectedAp = connectedAps.FirstOrDefault(x => string.Equals(Attr(x, "iedName"), iedName, StringComparison.OrdinalIgnoreCase));
        var apName = Attr(connectedAp, "apName", Attr(selectedIed.Element(ns + "AccessPoint"), "name", "AP1"));
        var ip = ReadAddressValue(connectedAp, ns, "IP");
        var portText = ReadAddressValue(connectedAp, ns, "IP-Port");
        var port = int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) && p > 0 ? p : 102;

        var result = new SclImportResult
        {
            FilePath = filePath,
            IedName = iedName,
            AccessPointName = apName,
            IpAddress = ip,
            MmsPort = port
        };

        var templates = BuildTemplates(root.Element(ns + "DataTypeTemplates"), ns);
        var accessPoints = selectedIed.Elements(ns + "AccessPoint").ToList();
        var selectedAp = accessPoints.FirstOrDefault(x => string.Equals(Attr(x, "name"), apName, StringComparison.OrdinalIgnoreCase)) ?? accessPoints.FirstOrDefault();
        var server = selectedAp?.Element(ns + "Server");
        if (server == null)
            throw new InvalidOperationException($"IED '{iedName}' has no AccessPoint/Server section in the SCL file.");

        foreach (var lDevice in server.Elements(ns + "LDevice"))
        {
            var ldInst = Attr(lDevice, "inst");
            if (string.IsNullOrWhiteSpace(ldInst)) continue;
            var ldName = ComposeLogicalDeviceName(iedName, ldInst, Attr(lDevice, "ldName"));

            foreach (var ln in lDevice.Elements().Where(e => e.Name.LocalName is "LN0" or "LN"))
            {
                var lnName = ComposeLogicalNodeName(ln);
                var lnType = Attr(ln, "lnType");
                if (!string.IsNullOrWhiteSpace(lnType) && templates.LNodeTypes.TryGetValue(lnType, out var lNodeType))
                {
                    foreach (var doDef in lNodeType.DataObjects)
                    {
                        foreach (var leaf in ExpandDoLeaves(templates, doDef.TypeId, doDef.Name, string.Empty, null, 0))
                        {
                            var objectRef = $"{ldName}/{lnName}.{leaf.Path}";
                            var signal = new SignalDefinition
                            {
                                Name = BuildSignalName(lnName, leaf.Path),
                                ObjectReference = objectRef,
                                FunctionalConstraint = leaf.FunctionalConstraint,
                                DataType = NormalizeDataType(leaf.BasicType),
                                Category = CategorizeSignal(objectRef),
                                Unit = GuessUnit(objectRef),
                                Confidence = "SCL",
                                Quality = "SCL imported",
                                IsSelected = SignalDefinition.IsCoreScadaSignal(objectRef, SignalDefinition.DetectLogicalNodeClass(SignalDefinitionExtractLogicalNode(objectRef)), NormalizeDataType(leaf.BasicType), CategorizeSignal(objectRef))
                            };
                            result.Signals.Add(signal);
                        }
                    }
                }

                foreach (var dataSet in ln.Elements(ns + "DataSet"))
                {
                    var ds = new SclDataSetModel
                    {
                        Name = Attr(dataSet, "name"),
                        LogicalDevice = ldName,
                        LogicalNode = lnName
                    };
                    ds.Reference = $"{ldName}/{lnName}.{ds.Name}";
                    foreach (var fcda in dataSet.Elements(ns + "FCDA"))
                    {
                        var member = CreateDataSetMember(iedName, fcda);
                        if (!string.IsNullOrWhiteSpace(member.ObjectReference))
                            ds.Members.Add(member);
                    }
                    result.DataSets.Add(ds);
                }

                foreach (var rc in ln.Elements(ns + "ReportControl"))
                {
                    var buffered = string.Equals(Attr(rc, "buffered"), "true", StringComparison.OrdinalIgnoreCase);
                    var dsName = Attr(rc, "datSet");
                    var report = new SclReportControlModel
                    {
                        Name = Attr(rc, "name"),
                        DataSetName = dsName,
                        ReportId = Attr(rc, "rptID"),
                        Buffered = buffered,
                        IntegrityPeriodMs = int.TryParse(Attr(rc, "intgPd"), out var intg) ? intg : 0,
                        TriggerOptions = ReadChildAttributes(rc.Element(ns + "TrgOps")),
                        OptionalFields = ReadChildAttributes(rc.Element(ns + "OptFields"))
                    };
                    report.Reference = $"{ldName}/{lnName}.{(buffered ? "BR" : "RP")}.{report.Name}";
                    report.DataSetReference = string.IsNullOrWhiteSpace(dsName) ? string.Empty : $"{ldName}/{lnName}.{dsName}";
                    result.ReportControls.Add(report);
                }
            }
        }

        MarkReportCapableSignals(result);
        return result;
    }

    private static XElement PickIed(List<XElement> ieds, List<XElement> connectedAps, XNamespace ns)
    {
        foreach (var cap in connectedAps)
        {
            var iedName = Attr(cap, "iedName");
            if (string.IsNullOrWhiteSpace(ReadAddressValue(cap, ns, "IP"))) continue;
            var match = ieds.FirstOrDefault(x => string.Equals(Attr(x, "name"), iedName, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;
        }
        return ieds.First();
    }

    private static string ReadAddressValue(XElement? connectedAp, XNamespace ns, string type)
    {
        if (connectedAp == null) return string.Empty;
        return connectedAp.Descendants(ns + "P")
            .FirstOrDefault(p => string.Equals(Attr(p, "type"), type, StringComparison.OrdinalIgnoreCase))
            ?.Value.Trim() ?? string.Empty;
    }

    private static SclTemplates BuildTemplates(XElement? root, XNamespace ns)
    {
        var templates = new SclTemplates();
        if (root == null) return templates;

        foreach (var lnt in root.Elements(ns + "LNodeType"))
        {
            var id = Attr(lnt, "id");
            if (string.IsNullOrWhiteSpace(id)) continue;
            templates.LNodeTypes[id] = new LNodeTypeDef
            {
                Id = id,
                LnClass = Attr(lnt, "lnClass"),
                DataObjects = lnt.Elements(ns + "DO")
                    .Select(x => new DataObjectDef { Name = Attr(x, "name"), TypeId = Attr(x, "type") })
                    .Where(x => !string.IsNullOrWhiteSpace(x.Name) && !string.IsNullOrWhiteSpace(x.TypeId))
                    .ToList()
            };
        }

        foreach (var dot in root.Elements(ns + "DOType"))
        {
            var id = Attr(dot, "id");
            if (string.IsNullOrWhiteSpace(id)) continue;
            var def = new DoTypeDef { Id = id, Cdc = Attr(dot, "cdc") };
            foreach (var da in dot.Elements())
            {
                if (da.Name.LocalName is not ("DA" or "SDO")) continue;
                def.Children.Add(new TemplateChild
                {
                    Name = Attr(da, "name"),
                    TypeId = Attr(da, "type"),
                    BasicType = da.Name.LocalName == "SDO" ? "Struct" : Attr(da, "bType"),
                    FunctionalConstraint = Attr(da, "fc"),
                    IsSubDataObject = da.Name.LocalName == "SDO"
                });
            }
            templates.DoTypes[id] = def;
        }

        foreach (var dat in root.Elements(ns + "DAType"))
        {
            var id = Attr(dat, "id");
            if (string.IsNullOrWhiteSpace(id)) continue;
            var def = new DaTypeDef { Id = id };
            foreach (var bda in dat.Elements(ns + "BDA"))
            {
                def.Children.Add(new TemplateChild
                {
                    Name = Attr(bda, "name"),
                    TypeId = Attr(bda, "type"),
                    BasicType = Attr(bda, "bType"),
                    FunctionalConstraint = string.Empty,
                    IsSubDataObject = false
                });
            }
            templates.DaTypes[id] = def;
        }

        return templates;
    }

    private static IEnumerable<SclLeaf> ExpandDoLeaves(SclTemplates templates, string typeId, string prefix, string inheritedFc, string? inheritedBasicType, int depth)
    {
        if (depth > 8) yield break;
        if (!templates.DoTypes.TryGetValue(typeId, out var doType)) yield break;

        foreach (var child in doType.Children)
        {
            if (string.IsNullOrWhiteSpace(child.Name)) continue;
            var path = string.IsNullOrWhiteSpace(prefix) ? child.Name : $"{prefix}.{child.Name}";
            var fc = string.IsNullOrWhiteSpace(child.FunctionalConstraint) ? inheritedFc : child.FunctionalConstraint;
            if (child.IsSubDataObject)
            {
                foreach (var leaf in ExpandDoLeaves(templates, child.TypeId, path, fc, child.BasicType, depth + 1))
                    yield return leaf;
                continue;
            }

            foreach (var leaf in ExpandDaLeaves(templates, child.TypeId, path, fc, child.BasicType, depth + 1))
                yield return leaf;
        }
    }

    private static IEnumerable<SclLeaf> ExpandDaLeaves(SclTemplates templates, string typeId, string path, string fc, string basicType, int depth)
    {
        if (depth > 10) yield break;
        if (IsPrimitiveBasicType(basicType) || string.IsNullOrWhiteSpace(typeId) || !templates.DaTypes.TryGetValue(typeId, out var daType))
        {
            yield return new SclLeaf { Path = path, FunctionalConstraint = fc, BasicType = basicType };
            yield break;
        }

        foreach (var child in daType.Children)
        {
            if (string.IsNullOrWhiteSpace(child.Name)) continue;
            var childPath = $"{path}.{child.Name}";
            foreach (var leaf in ExpandDaLeaves(templates, child.TypeId, childPath, fc, child.BasicType, depth + 1))
                yield return leaf;
        }
    }

    private static bool IsPrimitiveBasicType(string bType)
    {
        if (string.IsNullOrWhiteSpace(bType)) return true;
        return !bType.Equals("Struct", StringComparison.OrdinalIgnoreCase);
    }

    private static SclDataSetMember CreateDataSetMember(string iedName, XElement fcda)
    {
        var ldInst = Attr(fcda, "ldInst");
        var ldName = ComposeLogicalDeviceName(iedName, ldInst);
        var lnName = ComposeLogicalNodeName(Attr(fcda, "prefix"), Attr(fcda, "lnClass"), Attr(fcda, "lnInst"));
        var doName = Attr(fcda, "doName");
        var daName = Attr(fcda, "daName");
        var fc = Attr(fcda, "fc");
        var reference = string.IsNullOrWhiteSpace(daName)
            ? $"{ldName}/{lnName}.{doName}"
            : $"{ldName}/{lnName}.{doName}.{daName}";
        return new SclDataSetMember
        {
            ObjectReference = reference.Replace("..", "."),
            FunctionalConstraint = fc,
            OriginalText = $"ldInst={ldInst}; ln={lnName}; fc={fc}; do={doName}; da={daName}"
        };
    }

    private static void MarkReportCapableSignals(SclImportResult result)
    {
        foreach (var signal in result.Signals)
        {
            var match = result.DataSets.FirstOrDefault(ds => ds.Members.Any(m => IsMemberCoveringSignal(m, signal)));
            if (match == null) continue;
            signal.IsReportCapable = true;
            signal.DataSetReference = match.Reference;
            var rcb = result.ReportControls.FirstOrDefault(r => string.Equals(r.DataSetReference, match.Reference, StringComparison.OrdinalIgnoreCase));
            if (rcb != null)
                signal.ReportControlReference = rcb.Reference;
        }
    }

    private static bool IsMemberCoveringSignal(SclDataSetMember member, SignalDefinition signal)
    {
        if (!string.IsNullOrWhiteSpace(member.FunctionalConstraint) && !string.IsNullOrWhiteSpace(signal.FunctionalConstraint) &&
            !string.Equals(member.FunctionalConstraint, signal.FunctionalConstraint, StringComparison.OrdinalIgnoreCase))
            return false;

        var m = NormalizeReference(member.ObjectReference);
        var s = NormalizeReference(signal.ObjectReference);
        return s.Equals(m, StringComparison.OrdinalIgnoreCase) || s.StartsWith(m + ".", StringComparison.OrdinalIgnoreCase) || m.StartsWith(s + ".", StringComparison.OrdinalIgnoreCase);
    }

    private static string ComposeLogicalDeviceName(string iedName, string ldInst, string ldName = "")
    {
        // SCL can expose either ldName or only inst. For MMS objectReference, use the
        // exact logical-device name when ldName is supplied; otherwise fall back to the
        // common IEC 61850 convention IEDName + ldInst. Wrong LD naming is one of the
        // most common reasons a CID-imported point reads Bad/OBJECT_NONE_EXISTENT.
        if (!string.IsNullOrWhiteSpace(ldName)) return ldName.Trim();
        if (string.IsNullOrWhiteSpace(ldInst)) return iedName;
        if (ldInst.StartsWith(iedName, StringComparison.OrdinalIgnoreCase)) return ldInst;
        return $"{iedName}{ldInst}";
    }

    private static string ComposeLogicalNodeName(XElement ln) => ComposeLogicalNodeName(Attr(ln, "prefix"), Attr(ln, "lnClass", ln.Name.LocalName == "LN0" ? "LLN0" : string.Empty), Attr(ln, "inst"));

    private static string ComposeLogicalNodeName(string prefix, string lnClass, string inst)
    {
        if (string.Equals(lnClass, "LLN0", StringComparison.OrdinalIgnoreCase)) return "LLN0";
        return $"{prefix}{lnClass}{inst}";
    }

    private static string BuildSignalName(string lnName, string leafPath)
    {
        var last = leafPath.Split('.', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? leafPath;
        return $"{lnName} {leafPath.Replace('.', ' ')}".Trim();
    }

    private static string CategorizeSignal(string objectReference)
    {
        var r = objectReference.ToLowerInvariant();
        if (r.Contains(".pos.stval")) return "Position";
        if (r.Contains(".op.general") || r.Contains(".str.general") || r.Contains(".tr.general")) return "Protection";
        if (r.Contains(".cval.mag.f") || r.Contains(".instcval.mag.f")) return "Measurement";
        if (r.EndsWith(".q")) return "Quality";
        if (r.EndsWith(".t")) return "Timestamp";
        return "Raw";
    }

    private static string GuessUnit(string objectReference)
    {
        var r = objectReference.ToLowerInvariant();
        if (r.Contains(".a.")) return "A";
        if (r.Contains(".phv.") || r.Contains(".ppv.")) return "V";
        return string.Empty;
    }

    private static string NormalizeDataType(string basicType)
    {
        var t = (basicType ?? string.Empty).Trim();
        if (t.Equals("Dbpos", StringComparison.OrdinalIgnoreCase)) return "Dbpos";
        if (t.Equals("BOOLEAN", StringComparison.OrdinalIgnoreCase)) return "Boolean";
        if (t.Contains("FLOAT", StringComparison.OrdinalIgnoreCase)) return "Float32";
        if (t.StartsWith("INT", StringComparison.OrdinalIgnoreCase) || t.StartsWith("UINT", StringComparison.OrdinalIgnoreCase) || t.Equals("Enum", StringComparison.OrdinalIgnoreCase)) return "Integer";
        if (t.Equals("Quality", StringComparison.OrdinalIgnoreCase)) return "Quality";
        if (t.Equals("Timestamp", StringComparison.OrdinalIgnoreCase)) return "Timestamp";
        return string.IsNullOrWhiteSpace(t) ? "Unknown" : t;
    }

    private static string NormalizeReference(string reference) => (reference ?? string.Empty).Replace('$', '.').Replace("..", ".").Trim();

    private static string SignalDefinitionExtractLogicalNode(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return string.Empty;
        var slash = reference.IndexOf('/');
        if (slash < 0 || slash == reference.Length - 1) return string.Empty;
        var afterSlash = reference[(slash + 1)..];
        var dot = afterSlash.IndexOf('.');
        return dot > 0 ? afterSlash[..dot] : afterSlash;
    }

    private static string ReadChildAttributes(XElement? element)
    {
        if (element == null) return string.Empty;
        return string.Join(", ", element.Attributes().Where(a => a.Name.LocalName != "desc").Select(a => $"{a.Name.LocalName}={a.Value}"));
    }

    private static string Attr(XElement? element, string name, string fallback = "") => element?.Attribute(name)?.Value?.Trim() ?? fallback;

    private sealed class SclTemplates
    {
        public Dictionary<string, LNodeTypeDef> LNodeTypes { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, DoTypeDef> DoTypes { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, DaTypeDef> DaTypes { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class LNodeTypeDef
    {
        public string Id { get; set; } = string.Empty;
        public string LnClass { get; set; } = string.Empty;
        public List<DataObjectDef> DataObjects { get; set; } = new();
    }

    private sealed class DataObjectDef
    {
        public string Name { get; set; } = string.Empty;
        public string TypeId { get; set; } = string.Empty;
    }

    private sealed class DoTypeDef
    {
        public string Id { get; set; } = string.Empty;
        public string Cdc { get; set; } = string.Empty;
        public List<TemplateChild> Children { get; set; } = new();
    }

    private sealed class DaTypeDef
    {
        public string Id { get; set; } = string.Empty;
        public List<TemplateChild> Children { get; set; } = new();
    }

    private sealed class TemplateChild
    {
        public string Name { get; set; } = string.Empty;
        public string TypeId { get; set; } = string.Empty;
        public string BasicType { get; set; } = string.Empty;
        public string FunctionalConstraint { get; set; } = string.Empty;
        public bool IsSubDataObject { get; set; }
    }

    private sealed class SclLeaf
    {
        public string Path { get; set; } = string.Empty;
        public string FunctionalConstraint { get; set; } = string.Empty;
        public string BasicType { get; set; } = string.Empty;
    }
}
