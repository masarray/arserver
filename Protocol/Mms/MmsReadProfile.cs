namespace Ari61850Bridge.Protocol.Mms;

public enum MmsReadProfile
{
    PrimaryFcNamedVariable,
    AlternateNoFcNamedVariable
}

public sealed class MmsReadAttempt
{
    public MmsReadProfile Profile { get; init; }
    public MmsObjectReference Reference { get; init; }
    public MmsReadDecodeResult Result { get; init; } = new();

    public string Summary => $"{Profile}: {Reference} => {(Result.IsSuccess ? "OK" : Result.Message)}";
}
