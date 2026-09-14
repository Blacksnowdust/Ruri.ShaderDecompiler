namespace Ruri.ShaderTools.Spirv.Interstage;

/// <summary>
/// Gives every interstage variable a location of its own.
///
/// A D3D signature packs several semantics into one register — two
/// <c>float2</c> varyings share a register as <c>.xy</c> and <c>.zw</c> — and the
/// translator carries that faithfully as one SPIR-V <c>Location</c> with a
/// <c>Component</c> offset. The HLSL backend spells a location as a semantic and
/// ignores the component, so both varyings come out as <c>TEXCOORD2</c>, which
/// HLSL rejects as an overlap.
///
/// The rewrite is the bijection <c>location × 4 + component</c>: a scalar slot
/// index in the signature's own model. It is computed from each variable's own
/// decorations alone, so a vertex stage and the fragment stage that consumes it —
/// decompiled separately, possibly with different subsets of the varyings alive —
/// arrive at identical numbering without ever seeing each other.
///
/// Only interstage variables are touched: vertex INPUTS keep their location,
/// which is the join key to the container's input signature, and fragment
/// OUTPUTS keep theirs, which is the render target index.
/// </summary>
internal static class InterstageSlotAssigner
{
    private const uint ComponentsPerRegister = 4;

    private const uint VertexExecutionModel = 0;
    private const uint FragmentExecutionModel = 4;

    public static byte[] Assign(byte[] spirv)
    {
        SpirvModule module = SpirvModule.Parse(spirv);

        uint? executionModel = FindExecutionModel(module);
        if (executionModel is null)
        {
            return spirv;
        }

        HashSet<uint> interstage = CollectInterstageVariables(module, executionModel.Value);
        if (interstage.Count == 0)
        {
            return spirv;
        }

        var componentById = new Dictionary<uint, uint>();
        var componentDecorations = new List<SpirvInstruction>();
        var locationDecorations = new List<SpirvInstruction>();

        foreach (SpirvInstruction instruction in module.Instructions)
        {
            if (instruction.OpCode != SpvOpCode.OpDecorate || instruction.WordCount < 4 || !interstage.Contains(instruction[1]))
            {
                continue;
            }

            switch (instruction[2])
            {
                case Decoration.Component:
                    componentById[instruction[1]] = instruction[3];
                    componentDecorations.Add(instruction);
                    break;
                case Decoration.Location:
                    locationDecorations.Add(instruction);
                    break;
            }
        }

        if (locationDecorations.Count == 0)
        {
            return spirv;
        }

        foreach (SpirvInstruction location in locationDecorations)
        {
            uint component = componentById.TryGetValue(location[1], out uint value) ? value : 0;
            location[3] = (location[3] * ComponentsPerRegister) + component;
        }

        foreach (SpirvInstruction component in componentDecorations)
        {
            component.MakeNop();
        }

        return module.ToBytes();
    }

    private static uint? FindExecutionModel(SpirvModule module)
    {
        foreach (SpirvInstruction instruction in module.Instructions)
        {
            if (instruction.OpCode == SpvOpCode.OpEntryPoint && instruction.WordCount >= 3)
            {
                return instruction[1];
            }
        }

        return null;
    }

    private static HashSet<uint> CollectInterstageVariables(SpirvModule module, uint executionModel)
    {
        var interstage = new HashSet<uint>();

        foreach (SpirvInstruction instruction in module.Instructions)
        {
            if (instruction.OpCode != SpvOpCode.OpVariable || instruction.WordCount < 4)
            {
                continue;
            }

            uint storageClass = instruction[3];
            bool isInterstage =
                (storageClass == StorageClass.Output && executionModel != FragmentExecutionModel)
                || (storageClass == StorageClass.Input && executionModel != VertexExecutionModel);

            if (isInterstage)
            {
                interstage.Add(instruction[2]);
            }
        }

        return interstage;
    }
}
