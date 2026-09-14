using Ruri.ShaderTools.Pipeline.Backend;
using Ruri.ShaderTools.Pipeline.Diagnostics;
using Ruri.ShaderTools.Pipeline.Frontend;
using Ruri.ShaderTools.Pipeline.Naming;
using Ruri.ShaderTools.Spirv.ConstantBuffers;
using Ruri.ShaderTools.Spirv.Interstage;
using Ruri.ShaderTools.Spirv.ScalarLayout;
using Ruri.ShaderTools.Spirv.SymbolInjection;
using Ruri.ShaderTools.Unity;

namespace Ruri.ShaderTools.Pipeline;

/// <summary>
/// The decompile route, end to end:
///
/// <code>
///   binary → SPIR-V → scalar-layout normalise → structure constant buffers
///          → [host symbol enrichment] → inject symbols → plan emission → emit HLSL
/// </code>
///
/// Every step is engine-agnostic. Engine knowledge enters only through the
/// symbol table the caller builds, which is what lets one pipeline serve
/// unrelated engines without a branch anywhere in it.
///
/// NOT thread-safe: it holds per-call state (the structurer's resolved names and
/// log, the driver's last failure). Each worker owns its own instance — which is
/// cheap, since construction resolves no resources.
/// </summary>
internal sealed class DecompilePipeline
{
    /// <summary>
    /// Shader model floor for the source backend.
    ///
    /// The backend uses this value to gate WHICH INTRINSICS IT IS WILLING TO
    /// EMIT — never to validate input. Every gate is "emitting X requires SM ≥ N",
    /// so raising it can only unlock: wave ops, non-float texture sampling, mesh
    /// and variable-rate emission all fire on inputs compiled for older models.
    /// A caller asking for a HIGHER model keeps it — the floor only raises.
    /// </summary>
    private const uint MinimumEmitShaderModel = 67;

    private readonly ConstantBufferStructurer _structurer = new();
    private readonly SpirvCrossDriver _driver = new();
    private readonly SpirvFrontend _frontend = new();

    public DecompileResult Run(byte[] binary, DecompileOptions options)
    {
        SerializedProgramData symbols = options.Symbols ?? new SerializedProgramData();
        ShaderBinaryFormat format = ShaderBinaryFormatDetector.Detect(options.Format, binary);
        uint shaderModel = Math.Max(options.ShaderModel, MinimumEmitShaderModel);

        var result = new DecompileResult { FinalSymbols = symbols, FinalUnityMetadata = options.UnityMetadata };
        DecompileStage stage = DecompileStage.FrontendConversion;

        try
        {
            FrontendOutput frontend = _frontend.Convert(format, binary);

            stage = DecompileStage.ScalarLayoutNormalization;
            byte[] spirv = ScalarBlockVectorizer.Vectorize(frontend.Spirv);

            stage = DecompileStage.InterstageSlotAssignment;
            spirv = InterstageSlotAssigner.Assign(spirv);
            result.SpirvAfterFrontend = spirv;

            stage = DecompileStage.ConstantBufferStructuring;
            byte[] structured = Structure(spirv, symbols);
            result.SpirvAfterStructuring = structured;

            stage = DecompileStage.SymbolEnrichment;
            Enrich(options, structured, symbols);

            stage = DecompileStage.SymbolInjection;
            (byte[] injected, List<BlockMemberPrefix> prefixes) = Inject(structured, symbols);
            result.SpirvAfterSymbolInjection = injected;

            stage = DecompileStage.SourceEmission;
            EntryPointSelection entry = EntryPointResolver.Resolve(injected, symbols.EntryPoint);
            EmissionPlan plan = BuildPlan(entry, shaderModel, format, frontend.InputSignature, prefixes);
            string source = Emit(injected, symbols, plan);

            result.Success = true;
            result.FailedStage = DecompileStage.Completed;
            result.SourceCode = source;
            result.SourceLanguage = "hlsl";
            result.SourceFileExtension = ".hlsl";
            result.Stage = entry.Stage;
            result.FinalSpirv = injected;
            result.StructuringLog = _structurer.LastRewriteSummary;
            return result;
        }
        catch (Exception exception)
        {
            return Fail(result, stage, exception, binary, options, symbols);
        }
    }

    // Each wrapper below exists so the thrown message names the stage AND carries
    // the module state that explains it. A bare "emission failed" is unactionable;
    // the same message with the patch plan and built-in decorations attached
    // usually is not.

    private byte[] Structure(byte[] spirv, SerializedProgramData symbols)
    {
        try
        {
            return _structurer.Rewrite(spirv, symbols);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"Constant buffer structuring failed.{Environment.NewLine}{ModuleReports.DescribeBuiltInDecorations(spirv)}",
                exception);
        }
    }

    private static void Enrich(DecompileOptions options, byte[] structured, SerializedProgramData symbols)
    {
        if (options.SymbolEnricher is null)
        {
            return;
        }

        try
        {
            options.SymbolEnricher(structured, symbols);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException($"SymbolEnricher threw: {exception.Message}", exception);
        }
    }

    private (byte[] Spirv, List<BlockMemberPrefix> Prefixes) Inject(byte[] spirv, SerializedProgramData symbols)
    {
        try
        {
            // Mark the members nothing will ever have a name for FIRST, so the
            // scan below sees them and the real names injected afterwards
            // overwrite whichever of them turn out to be recoverable.
            spirv = AnonymousMemberNamer.Apply(spirv);

            var prefixes = new List<BlockMemberPrefix>();
            if (symbols.GetResourceBindingCount() == 0)
            {
                return (spirv, prefixes);
            }

            List<DescriptorBindingInfo> bindings = BindingScanner.Scan(spirv);
            List<NamePatch> names = new ResourceNamePlanner(_structurer.GetResolvedBlockName).Plan(bindings, symbols);
            List<MemberNamePatch> members = new BlockMemberNamePlanner(_structurer.GetResolvedBlockName).Plan(bindings, symbols);

            CollectBlockMemberPrefixes(bindings, names, members, prefixes);
            return (DebugNameInjector.Inject(spirv, names, members), prefixes);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"Symbol injection failed.{Environment.NewLine}" +
                $"{ModuleReports.DescribePatchPlan(spirv, symbols, _structurer.GetResolvedBlockName)}{Environment.NewLine}" +
                $"{ModuleReports.DescribeBuiltInDecorations(spirv)}",
                exception);
        }
    }

    /// <summary>
    /// Pair every constant-buffer variable's injected name with the member names
    /// injected into its struct. The backend will spell each member as
    /// <c>&lt;variable&gt;_&lt;member&gt;</c>; recording the pair here is what lets
    /// the driver undo that as an exact token rather than a search.
    /// </summary>
    private static void CollectBlockMemberPrefixes(
        List<DescriptorBindingInfo> bindings,
        List<NamePatch> names,
        List<MemberNamePatch> members,
        List<BlockMemberPrefix> prefixes)
    {
        foreach (DescriptorBindingInfo binding in bindings)
        {
            if (binding.Kind != DescriptorKind.UniformBuffer || binding.StructTypeId is not uint structTypeId)
            {
                continue;
            }

            string? variableName = null;
            foreach (NamePatch patch in names)
            {
                if (patch.Id == binding.Id)
                {
                    variableName = patch.Name;
                    break;
                }
            }

            if (variableName is null)
            {
                continue;
            }

            var memberNames = new List<string>();
            foreach (MemberNamePatch patch in members)
            {
                if (patch.StructTypeId == structTypeId)
                {
                    memberNames.Add(patch.Name);
                }
            }

            if (memberNames.Count > 0)
            {
                prefixes.Add(new BlockMemberPrefix(variableName, memberNames));
            }
        }
    }

    /// <summary>
    /// Vertex semantics come from the container's own input signature when there
    /// is one. A bare SPIR-V input has no signature — it carries only locations,
    /// which Unity assigned in its fixed attribute order, so that order is the
    /// semantic. System values are skipped: the backend already emits their
    /// built-in semantic and a remap would only collide with it.
    /// </summary>
    private static EmissionPlan BuildPlan(
        EntryPointSelection entry,
        uint shaderModel,
        ShaderBinaryFormat format,
        IReadOnlyList<InputSignatureElement> signature,
        List<BlockMemberPrefix> prefixes)
    {
        var plan = new EmissionPlan { EntryPoint = entry, ShaderModel = shaderModel };
        plan.BlockMemberPrefixes.AddRange(prefixes);

        if (entry.Stage != PipelineStage.Vertex)
        {
            return plan;
        }

        if (signature.Count > 0)
        {
            foreach (InputSignatureElement element in signature)
            {
                if (element.SemanticName.StartsWith("SV_", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                plan.VertexAttributes.Add(new VertexAttributeSemantic(element.Register, element.SemanticName + element.SemanticIndex));
            }
        }
        else if (format == ShaderBinaryFormat.SpirV)
        {
            UnityVertexAttributeOrder.AppendAll(plan.VertexAttributes);
        }

        return plan;
    }

    private string Emit(byte[] spirv, SerializedProgramData symbols, EmissionPlan plan)
    {
        string? source = _driver.Emit(spirv, plan);
        if (source is not null)
        {
            return source;
        }

        throw new InvalidOperationException(
            $"Source emission failed after symbol injection. {_driver.LastFailure}{Environment.NewLine}" +
            $"{ModuleReports.DescribePatchPlan(spirv, symbols, _structurer.GetResolvedBlockName)}{Environment.NewLine}" +
            $"{ModuleReports.DescribeBuiltInDecorations(spirv)}");
    }

    private DecompileResult Fail(
        DecompileResult result,
        DecompileStage stage,
        Exception exception,
        byte[] binary,
        DecompileOptions options,
        SerializedProgramData symbols)
    {
        result.Success = false;
        result.ErrorMessage = exception.ToString();
        result.FailedStage = stage;
        result.FinalSpirv = result.SpirvAfterSymbolInjection ?? result.SpirvAfterStructuring ?? result.SpirvAfterFrontend;
        result.StructuringLog = _structurer.LastRewriteSummary;
        result.NativeToolDiagnostics = _frontend.LastFailure ?? _driver.LastFailure;

        // Report against the deepest module that exists: an earlier snapshot would
        // describe a state the failure did not happen in.
        byte[]? reportable = result.FinalSpirv;
        if (reportable is not null)
        {
            result.PatchPlanReport = ModuleReports.DescribePatchPlan(reportable, symbols, _structurer.GetResolvedBlockName);
            result.BuiltInDecorationReport = ModuleReports.DescribeBuiltInDecorations(reportable);
        }

        if (!string.IsNullOrWhiteSpace(options.DebugDumpDirectory))
        {
            try
            {
                result.DebugDumpDirectory = FailureDumpWriter.Write(
                    options.DebugDumpDirectory!, options.DebugDumpStem, binary, result, symbols);
            }
            catch (Exception dumpException)
            {
                Console.Error.WriteLine($"[ShaderDecompiler] Failed to write failure dump: {dumpException.Message}");
            }
        }

        return result;
    }
}
