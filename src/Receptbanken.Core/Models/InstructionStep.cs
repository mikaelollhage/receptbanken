namespace Receptbanken.Core.Models;

public class InstructionStep
{
    public int Id { get; set; }
    public int RecipeId { get; set; }
    public Recipe? Recipe { get; set; }
    public int StepNumber { get; set; }
    public string InstructionText { get; set; } = string.Empty;
    public string? GroupHeader { get; set; }
}

