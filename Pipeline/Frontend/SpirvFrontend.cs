using Ruri.ShaderTools.Pipeline.Native;

namespace Ruri.ShaderTools.Pipeline.Frontend;

/// <summary>What the front end recovered: the module, and the container's own input signature when it had one.</summary>
internal readonly record struct FrontendOutput(byte[] Spirv, IReadOnlyList<InputSignatureElement> InputSignature);

/// <summary>
/// Compiled shader binary → SPIR-V, the pipeline's single intermediate form.
///
/// Everything downstream speaks only SPIR-V. This is the one place that knows
/// any other format exists, which is why adding a new input container means
/// adding a case here and nothing else.
///
/// A DXBC container's input signature is read alongside the conversion because
/// translation keeps only a numeric location per input; the signature is the
/// only surviving record of which semantic each location was.
/// </summary>
public sealed class SpirvFrontend
{
    /// <summary>
    /// Convert to pipeline-normalised SPIR-V for a host's own analysis. Returns
    /// null instead of throwing so bulk scans skip a bad blob rather than unwind.
    /// Layout normalisation is included: the legacy front end lowers a constant
    /// buffer to a scalar array, and a caller reading indices as vec4 registers
    /// without this step is silently off by four.
    /// </summary>
    public static byte[]? TryConvert(byte[] binary, out string? error)
    {
        error = null;
        if (binary is null || binary.Length == 0)
        {
            error = "Shader binary is empty.";
            return null;
        }

        try
        {
            var frontend = new SpirvFrontend();
            FrontendOutput output = frontend.Convert(ShaderBinaryFormatDetector.Detect(ShaderBinaryFormat.Unknown, binary), binary);
            return Spirv.ScalarLayout.ScalarBlockVectorizer.Vectorize(output.Spirv);
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return null;
        }
    }

    public string? LastFailure { get; private set; }

    internal FrontendOutput Convert(ShaderBinaryFormat format, byte[] binary) => format switch
    {
        ShaderBinaryFormat.Dxbc => ConvertContainer(binary, rawLlvm: false),
        ShaderBinaryFormat.Dxil => ConvertContainer(binary, ShaderBinaryFormatDetector.IsRawLlvmBitcode(binary)),
        ShaderBinaryFormat.SpirV => new FrontendOutput(binary, Array.Empty<InputSignatureElement>()),
        _ => throw new InvalidOperationException($"Unsupported shader format: {format}"),
    };

    private FrontendOutput ConvertContainer(byte[] container, bool rawLlvm)
    {
        if (!rawLlvm && !ShaderBinaryFormatDetector.IsDxbc(container))
        {
            throw new InvalidOperationException("Input does not contain a valid DXBC payload.");
        }

        byte[]? spirv = DxilSpirvLibrary.Convert(container, rawLlvm, out string? error);
        if (spirv is null)
        {
            LastFailure = error;
            throw new InvalidOperationException($"dxil-spirv did not produce a SPIR-V module. {error}");
        }

        return new FrontendOutput(spirv, DxbcInputSignature.Read(container));
    }
}
