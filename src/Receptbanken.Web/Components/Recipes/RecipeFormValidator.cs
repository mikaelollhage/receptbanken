using Receptbanken.Core.Models;

namespace Receptbanken.Web.Components.Recipes;

public static class RecipeFormValidator
{
    public const string TitleRequiredMessage = "Ange en titel för receptet.";
    public const string ServingsInvalidMessage = "Antal portioner måste vara minst 1.";

    public static bool IsTitleValid(Recipe? recipe) =>
        recipe != null && !string.IsNullOrWhiteSpace(recipe.Title);

    public static bool IsServingsValid(Recipe? recipe) =>
        recipe != null && recipe.Servings >= 1;

    public static List<string> Validate(Recipe? recipe)
    {
        var errors = new List<string>();
        if (!IsTitleValid(recipe))
        {
            errors.Add(TitleRequiredMessage);
        }
        if (!IsServingsValid(recipe))
        {
            errors.Add(ServingsInvalidMessage);
        }
        return errors;
    }
}

