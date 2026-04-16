namespace Reflag.Internal;

internal sealed class CompiledFlagDefinition
{
    public FlagDefinition Definition { get; init; } = new();

    public Func<IReadOnlyDictionary<string, object?>, string, EvaluationResult<bool>> Evaluator { get; init; } =
        static (_, flagKey) => new EvaluationResult<bool>
        {
            FlagKey = flagKey,
            Value = false,
        };
}
