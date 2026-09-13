namespace Ruri.ShaderTools;

// Persistent user-configurable settings for the shader decompiler
// surface. Read/written by the host UI layer (FModel hook tab,
// RipperHookGUI settings menu) via Ruri.Hook.Config.HookModuleSettings
// keyed on `ModuleKey` below. Adding a new field here is non-breaking:
// `HookModuleSettings.Get<T>` round-trips unknown JSON nodes so older
// settings files load forward and newer files load back without losing
// data on round-trip.
public sealed class ShaderDecompilerSettings
{
    /// <summary>
    /// Module key string the host UI uses when calling
    /// `HookModuleSettings.Get<ShaderDecompilerSettings>(ModuleKey)`.
    /// </summary>
    public const string ModuleKey = "ShaderDecompiler";

    /// <summary>
    /// When true (default), every program body is emitted as its own
    /// `<stem>/<variantKey>.hlsl` file and the .shader file `#include`s
    /// them, one per `#if defined(KEYWORD)` branch where there is a chain.
    /// When false, every body stays inline inside the .shader file.
    ///
    /// Every body of a shader that compiled to more than one, not just the
    /// ones sharing a stage with others: a shader with no multi_compile at
    /// all still has thousands of passes, and asking "is there a chain to
    /// slim down" answered no for all of them and wrote no files at all for
    /// exactly the shaders that needed them. A shader that compiled to a
    /// single body stays one file.
    ///
    /// Default is true: multi-variant URP/HDRP shaders (30+ × 30+
    /// variants per pass) produce a .shader so large that Unity's
    /// importer stalls when kept inline — splitting keeps each .shader
    /// file editor-responsive at the cost of single-file convenience.
    /// </summary>
    public bool SplitVariantsToHlslFiles { get; set; } = true;

    /// <summary>
    /// When true (default), the FModel-side decompile hook pops a
    /// warning before the first export of an `.ushaderbytecode`
    /// archive when no `.usmap` mappings are loaded — the resulting
    /// `.shader` files lose all author-facing parameter names. Set
    /// false to skip the prompt entirely (e.g. for headless batch
    /// export setups that never load mappings on purpose).
    /// </summary>
    public bool WarnIfNoMappings { get; set; } = true;

    /// <summary>
    /// When true (default), if no engine-UB metadata folder matches the
    /// game's exact EGame name (e.g. GAME_InfinityNikki), the loader falls
    /// back to the base UE folder (e.g. GAME_UE5_4). Most games (~99%)
    /// don't customize CB layouts, so this fallback is virtually always
    /// correct and significantly reduces the amount of manual seeding
    /// required.
    /// </summary>
    public bool TryMatchBaseEngineVersion { get; set; } = true;
}
