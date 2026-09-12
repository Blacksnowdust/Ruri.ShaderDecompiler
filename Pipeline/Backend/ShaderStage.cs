using Ruri.ShaderTools;

namespace Ruri.ShaderTools.Pipeline.Backend;

internal static class ShaderStageClassifier
{
    /// <summary>
    /// Map a raw SPIR-V execution model to a stage.
    ///
    /// The ray-tracing models appear TWICE in the numbering: 5267..5272 are the
    /// KHR values and 5313..5318 the NV-flavoured aliases with identical
    /// semantics. Real-world modules carry the NV numbers while disassemblers
    /// print the KHR friendly names, so the discrepancy is invisible unless you
    /// read raw words — and handling only one set silently misroutes every
    /// ray-tracing shader.
    /// </summary>
    public static PipelineStage FromExecutionModel(uint model) => model switch
    {
        0 => PipelineStage.Vertex,
        1 => PipelineStage.TessControl,
        2 => PipelineStage.TessEvaluation,
        3 => PipelineStage.Geometry,
        4 => PipelineStage.Fragment,
        5 => PipelineStage.Compute,
        5267 or 5313 => PipelineStage.RayGeneration,
        5268 or 5314 => PipelineStage.Intersection,
        5269 or 5315 => PipelineStage.AnyHit,
        5270 or 5316 => PipelineStage.ClosestHit,
        5271 or 5317 => PipelineStage.Miss,
        5272 or 5318 => PipelineStage.Callable,
        5364 => PipelineStage.Task,
        5365 => PipelineStage.Mesh,
        _ => PipelineStage.Unknown,
    };

    /// <summary>
    /// Stages whose built-ins an HLSL backend cannot represent at all, so the
    /// HLSL attempt is skipped rather than made and discarded.
    /// </summary>
    public static bool RequiresGlsl(PipelineStage stage) => stage is
        PipelineStage.RayGeneration
        or PipelineStage.Intersection
        or PipelineStage.AnyHit
        or PipelineStage.ClosestHit
        or PipelineStage.Miss
        or PipelineStage.Callable
        or PipelineStage.Task
        or PipelineStage.Mesh;
}
