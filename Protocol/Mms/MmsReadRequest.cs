using System;
using Ari61850Bridge.Protocol.Asn1;

namespace Ari61850Bridge.Protocol.Mms;

public static class MmsReadRequest
{
    /// <summary>
    /// Builds a compact MMS Confirmed-Read request for one domain-specific named variable.
    /// The returned payload includes the ISO Presentation Data prefix used after ACSE association.
    /// </summary>
    public static byte[] BuildSingleVariableRead(int invokeId, MmsObjectReference reference)
    {
        if (string.IsNullOrWhiteSpace(reference.Domain))
            throw new ArgumentException("MMS domain is empty. Use an SCL/IEC object reference such as LD0/LLN0.Mod.stVal.", nameof(reference));
        if (string.IsNullOrWhiteSpace(reference.Item))
            throw new ArgumentException("MMS item is empty.", nameof(reference));

        var domainId = VisibleString(reference.Domain);
        var itemId = VisibleString(reference.Item);

        // ObjectName.domain-specific [1] ::= SEQUENCE { domainId Identifier, itemId Identifier }
        var domainSpecific = Wrap(0xA1, Concat(domainId, itemId));

        // VariableSpecification.name [0] ObjectName
        var variableName = Wrap(0xA0, domainSpecific);

        // listOfVariable [0] IMPLICIT SEQUENCE OF VariableSpecification
        var listOfVariable = Wrap(0xA0, variableName);

        // Read-Request ::= SEQUENCE { variableAccessSpecification VariableAccessSpecification }
        // ConfirmedServiceRequest.read [4]
        var readRequest = Wrap(0xA4, listOfVariable);

        // Confirmed-RequestPDU [0] ::= SEQUENCE { invokeID Unsigned32, confirmedServiceRequest ... }
        var invoke = Integer(invokeId);
        var confirmedRequest = Wrap(0xA0, Concat(invoke, readRequest));

        // Presentation Data Values profile normally used for IEC 61850 MMS after association.
        // 01 00 01 00 = CP-type/fully-encoded-data single presentation-context selector profile.
        return Concat(new byte[] { 0x01, 0x00, 0x01, 0x00 }, confirmedRequest);
    }

    private static byte[] Integer(int value)
    {
        if (value < 0 || value > 0x7FFFFFFF) throw new ArgumentOutOfRangeException(nameof(value));
        if (value <= 0x7F) return new byte[] { 0x02, 0x01, (byte)value };
        if (value <= 0xFF) return new byte[] { 0x02, 0x02, 0x00, (byte)value };
        if (value <= 0x7FFF) return new byte[] { 0x02, 0x02, (byte)(value >> 8), (byte)value };
        if (value <= 0xFFFF) return new byte[] { 0x02, 0x03, 0x00, (byte)(value >> 8), (byte)value };
        return new byte[] { 0x02, 0x04, (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value };
    }

    private static byte[] VisibleString(string text)
        => Wrap(0x1A, System.Text.Encoding.ASCII.GetBytes(text));

    private static byte[] Wrap(byte tag, byte[] value)
    {
        var writer = new BerWriter();
        writer.WriteTlv(tag, value);
        return writer.ToArray();
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var length = 0;
        foreach (var part in parts) length += part.Length;
        var result = new byte[length];
        var offset = 0;
        foreach (var part in parts)
        {
            Buffer.BlockCopy(part, 0, result, offset, part.Length);
            offset += part.Length;
        }
        return result;
    }
}
