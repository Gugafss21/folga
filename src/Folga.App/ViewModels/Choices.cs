using Folga.Core.Overlay;
using Folga.Core.Profiles;
using Folga.Core.SystemSettings;

namespace Folga.App.ViewModels;

/// <summary>Opção de lista suspensa; Hint aparece ao passar o mouse sobre ela.</summary>
public sealed record Choice<T>(T Value, string Label, string Hint);

/// <summary>Prioridade de uma regra de fundo; Unchanged = não mexer.</summary>
public enum PriorityChoice { Unchanged, Idle, BelowNormal, Normal, AboveNormal, High }

public static class Choices
{
    public static IReadOnlyList<Choice<PriorityLevel>> GamePriorities { get; } =
    [
        new(PriorityLevel.Normal, "Normal", "Não muda a prioridade do jogo; o Folga só segura os apps de fundo."),
        new(PriorityLevel.AboveNormal, "Acima do normal", "O Windows atende o jogo antes dos apps comuns. Opção mais suave que Alta."),
        new(PriorityLevel.High, "Alta", "O Windows atende o jogo antes de quase tudo (recomendado). Realtime nunca é usado: pode travar o PC."),
    ];

    public static IReadOnlyList<Choice<PriorityChoice>> BackgroundPriorities { get; } =
    [
        new(PriorityChoice.Unchanged, "Não mexer", "Mantém a prioridade que o app já tem."),
        new(PriorityChoice.Idle, "Ociosa", "O app só roda quando sobra CPU. Pode deixar o app bem lento enquanto o jogo estiver aberto."),
        new(PriorityChoice.BelowNormal, "Abaixo do normal", "O app cede a CPU para o jogo sem parar de funcionar (recomendado)."),
        new(PriorityChoice.Normal, "Normal", "Força a prioridade normal durante o jogo."),
    ];

    public static IReadOnlyList<Choice<GameCores>> GameCoreModes { get; } =
    [
        new(GameCores.All, "Todos", "O jogo pode usar todos os núcleos, inclusive os que o fundo usa (recomendado)."),
        new(GameCores.ExcludeBackground, "Sem os do fundo",
            "O jogo fica só nos núcleos que não foram dados ao fundo. Separa bem, mas o jogo perde parte da CPU: teste jogo a jogo."),
    ];

    public static IReadOnlyList<Choice<BackgroundCores>> BackgroundCoreModes { get; } =
    [
        new(BackgroundCores.All, "Todos (não mexer)", "O app continua podendo usar qualquer núcleo."),
        new(BackgroundCores.Auto, "Automático",
            "Em CPU Intel com núcleos E, o app vai para eles; nas outras, para o último núcleo. Recomendado."),
        new(BackgroundCores.LastCore, "Último núcleo", "O app fica preso ao último núcleo (2 threads), longe do jogo."),
        new(BackgroundCores.LastTwoCores, "Últimos 2 núcleos",
            "O app fica nos 2 últimos núcleos (4 threads). Mais folga para o app, menos para o jogo."),
    ];

    public static IReadOnlyList<Choice<OverlayLayout>> OverlayLayouts { get; } =
    [
        new(OverlayLayout.Lines, "Um por linha", "Cada número numa linha, em coluna. Fácil de ler, ocupa mais altura."),
        new(OverlayLayout.OneLine, "Tudo numa linha", "Os números lado a lado. Discreto, bom para o canto de cima."),
    ];

    public static IReadOnlyList<Choice<OverlayCorner>> OverlayCorners { get; } =
    [
        new(OverlayCorner.TopLeft, "Superior esquerdo", "Canto de cima à esquerda (o padrão do RTSS)."),
        new(OverlayCorner.TopRight, "Superior direito", "Canto de cima à direita."),
        new(OverlayCorner.BottomLeft, "Inferior esquerdo", "Canto de baixo à esquerda."),
        new(OverlayCorner.BottomRight, "Inferior direito", "Canto de baixo à direita."),
    ];

    public static IReadOnlyList<Choice<PagefileMode>> PagefileModes { get; } =
    [
        new(PagefileMode.SystemManaged, "Gerenciada pelo Windows",
            "O Windows aumenta e diminui o arquivo de paginação conforme precisa. É o recomendado para quase todo mundo: nunca falta memória e não ocupa espaço à toa."),
        new(PagefileMode.Custom, "Tamanho fixo",
            "Você escolhe o mínimo e o máximo em MB. Evita o arquivo crescer no meio do jogo, mas se o máximo for pequeno, jogos e apps podem fechar por falta de memória."),
        new(PagefileMode.None, "Desligada",
            "Sem arquivo de paginação. Não deixa o jogo mais rápido: o limite de memória passa a ser só a RAM, e quando ela enche o jogo fecha. Não recomendado com 16 GB."),
    ];

    public static PriorityChoice From(PriorityLevel? level) =>
        level is { } l ? (PriorityChoice)((int)l + 1) : PriorityChoice.Unchanged;

    public static PriorityLevel? ToLevel(PriorityChoice choice) =>
        choice == PriorityChoice.Unchanged ? null : (PriorityLevel)((int)choice - 1);
}
