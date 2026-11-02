using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using Receptbanken.Core.Models;
using Receptbanken.Web.Components.Recipes;

namespace Receptbanken.Tests.Components;

public class RecipeInstructionEditorTests : TestContext
{
    public RecipeInstructionEditorTests()
    {
        Services.AddFluentUIComponents();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void RecipeInstructionEditor_AddButton_ShouldBeRenderedAtTheBottom_AfterAllInstructionRows()
    {
        var instructions = new List<InstructionStep>
        {
            new() { StepNumber = 1, InstructionText = "Steg ett: koka upp vatten." },
            new() { StepNumber = 2, InstructionText = "Steg två: lägg i pastan." },
            new() { StepNumber = 3, InstructionText = "Steg tre: häll av vattnet." }
        };

        var cut = RenderComponent<RecipeInstructionEditor>(parameters => parameters
            .Add(p => p.Instructions, instructions)
            .Add(p => p.HeadingLevel, 3)
            .Add(p => p.AddButtonText, "+ Lägg till steg"));

        // Header section should NOT contain the Add button
        var headerTitle = cut.Find("h3.form-section-title");
        var headerParent = headerTitle.ParentElement!;
        headerParent.QuerySelectorAll("button").Should().BeEmpty();

        // Instruction rows
        var rows = cut.FindAll(".instruction-editor-row");
        rows.Should().HaveCount(3);

        // Add button exists in the container following the instruction list
        var addBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Lägg till steg"));
        addBtn.Should().NotBeNull();
        addBtn.TextContent.Trim().Should().Be("+ Lägg till steg");
    }

    [Fact]
    public void RecipeInstructionEditor_ClickingAddButton_ShouldAppendNewStepAtTheEnd()
    {
        var instructions = new List<InstructionStep>
        {
            new() { StepNumber = 1, InstructionText = "Första steget." }
        };

        var cut = RenderComponent<RecipeInstructionEditor>(parameters => parameters
            .Add(p => p.Instructions, instructions));

        cut.FindAll(".instruction-editor-row").Should().HaveCount(1);

        // Click Add button
        var addBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Lägg till"));
        addBtn.Click();

        cut.WaitForAssertion(() =>
        {
            instructions.Should().HaveCount(2);
            cut.FindAll(".instruction-editor-row").Should().HaveCount(2);
            instructions[1].InstructionText.Should().BeEmpty();
        });
    }
}
