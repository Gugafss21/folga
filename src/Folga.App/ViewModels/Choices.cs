using Folga.Core.Profiles;

namespace Folga.App.ViewModels;

public sealed record Choice<T>(T Value, string Label);

/// <summary>Prioridade de uma regra de fundo; Unchanged = não mexer.</summary>
public enum PriorityChoice { Unchanged, Idle, BelowNormal, Normal, AboveNormal, High }

public static class Choices
{
    public static IReadOnlyList<Choice<PriorityLevel>> GamePriorities { get; } =
    [
        new(PriorityLevel.Normal, "Normal"),
        new(PriorityLevel.AboveNormal, "Acima do normal"),
        new(PriorityLevel.High, "Alta"),
    ];

    public static IReadOnlyList<Choice<PriorityChoice>> BackgroundPriorities { get; } =
    [
        new(PriorityChoice.Unchanged, "Não mexer"),
        new(PriorityChoice.Idle, "Ociosa"),
        new(PriorityChoice.BelowNormal, "Abaixo do normal"),
        new(PriorityChoice.Normal, "Normal"),
    ];

    public static IReadOnlyList<Choice<GameCores>> GameCoreModes { get; } =
    [
        new(GameCores.All, "Todos"),
        new(GameCores.ExcludeBackground, "Sem os do fundo"),
    ];

    public static IReadOnlyList<Choice<BackgroundCores>> BackgroundCoreModes { get; } =
    [
        new(BackgroundCores.All, "Todos (não mexer)"),
        new(BackgroundCores.Auto, "Automático"),
        new(BackgroundCores.LastCore, "Último núcleo"),
        new(BackgroundCores.LastTwoCores, "Últimos 2 núcleos"),
    ];

    public static PriorityChoice From(PriorityLevel? level) =>
        level is { } l ? (PriorityChoice)((int)l + 1) : PriorityChoice.Unchanged;

    public static PriorityLevel? ToLevel(PriorityChoice choice) =>
        choice == PriorityChoice.Unchanged ? null : (PriorityLevel)((int)choice - 1);
}
