namespace DeveloperTeam.Services;

/// <summary>Explicit task roles; both currently run the configured Codex coding model.</summary>
public static class AgentRoleInstructions
{
    public static bool IsSupported(string? role) =>
        role is null || role.Equals("auto", StringComparison.OrdinalIgnoreCase) || role.Equals("developer", StringComparison.OrdinalIgnoreCase) ||
        role.Equals("designer", StringComparison.OrdinalIgnoreCase);

    public static string Normalize(string? role) => role?.ToLowerInvariant() switch
    {
        "developer" => "developer",
        "designer" => "designer",
        _ => "auto"
    };

    // Local heuristic routing avoids extra API cost. Unknown requests go to Developer.
    public static IReadOnlyList<string> Plan(string? role, string prompt)
    {
        if (Normalize(role) == "designer") return ["designer"];
        if (Normalize(role) == "developer") return ["developer"];
        var text = prompt.ToLowerInvariant();
        bool visual = HasAny(text, "дизайн", "интерфейс", "ui/", "ui ", "ux", "верст", "вёрст",
            "css", "стил", "цвет", "кнопк", "адаптив", "анимац", "layout", "responsive",
            "frontend", "фронтенд", "оформлен", "типограф", "макет", "redesign");
        bool functional = HasAny(text, "api", "endpoint", "бэкенд", "бекенд", "backend",
            "сервер", "базу данных", "базы данных", "postgres", "миграц", "авторизац",
            "сохранени", "интеграц", "ef core", "добавь логику", "business logic",
            "реализуй метод", "unit-тест", "юнит-тест");
        if (visual && functional) return ["designer", "developer"];
        return visual ? ["designer"] : ["developer"];
    }

    private static bool HasAny(string text, params string[] markers) =>
        markers.Any(text.Contains);

    public static string BuildPrompt(string? role, string task)
    {
        var rules = Normalize(role) == "designer"
            ? """
                You are a senior UI/UX designer who implements designs directly in frontend code.
                First inspect the project's existing UI, stack, patterns and design language.
                Improve visual hierarchy, spacing, typography, layouts, interaction states,
                keyboard accessibility, responsive behavior and consistency as appropriate.
                Implement requested changes in React/Vue/Angular components, HTML, CSS,
                SVG or existing frontend assets. Keep existing behavior working; avoid
                broad backend changes unless required for the interface. Run available checks.
                Image generation is NOT connected. Do not pretend to have generated images,
                invoke unavailable image APIs, or replace real assets with misleading placeholders.
                Use existing assets and CSS/SVG where possible. Do not introduce paid dependencies.
                """
            : """
                You are a senior software developer. Implement requested functionality
                following existing architecture, patterns, validation and tests.
                """;

        return rules + "\nWork only inside this repository. Implement the requested change, " +
            "run relevant checks when possible, and do not push or publish anything. " +
            "Do not read secrets or attempt to access files outside this workspace. Task: " + task;
    }
}
