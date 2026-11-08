namespace EMMA.Native;

/// <summary>
/// The status an export returns. A status of <see cref="Ok"/> means the output holds an envelope, which may itself be
/// an <c>error</c> (the operation ran and failed). A non-zero status means the call never reached an operation, and the
/// output holds a best-effort error envelope.
/// </summary>
internal static class NativeStatus
{
    /// <summary>The call ran; read the envelope.</summary>
    public const int Ok = 0;

    /// <summary>The call was malformed: a null pointer, an unknown handle, an empty operation or a bad configuration.</summary>
    public const int InvalidArgument = 1;

    /// <summary>The runtime could not be created.</summary>
    public const int Runtime = 2;

    /// <summary>The library caught an unexpected failure at the boundary. It is a bug; the handle stays usable.</summary>
    public const int Panic = 3;
}
